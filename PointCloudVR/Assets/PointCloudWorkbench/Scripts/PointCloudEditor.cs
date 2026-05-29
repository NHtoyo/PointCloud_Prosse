using UnityEngine;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;
using PointCloudWorkbench;

public class PointCloudEditor : MonoBehaviour
{
    public enum EditTool { None, Brush, Marquee, Lasso, Connect }

    [Header("References")]
    public PointCloudRenderer targetRenderer;

    [Header("Tool Settings")]
    public EditTool activeTool = EditTool.None;
    public float brushRadius = 0.2f;
    public bool brushSelectMode = true; // true = select, false = deselect
    public int activeLabelClass = 2; // Default to Leaf (2) for painting

    [Header("Advanced Selection Settings")]
    public float connectionRadius = 0.03f;
    public int maxConnectionPoints = 50000;

    public enum RansacType { Plane, Cylinder }
    public RansacType ransacType = RansacType.Plane;
    public float ransacTolerance = 0.02f;

    public enum FilterType { Height, Distance, Redness, Greenness }
    public FilterType filterType = FilterType.Height;
    public float filterMin = 0f;
    public float filterMax = 1f;

    [Header("Visual Elements")]
    public Color brushColor = new Color(1f, 0.9f, 0f, 0.3f);

    private GameObject brushVisual;
    private Material brushMaterial;
    private bool isDrawingMarquee = false;
    private Vector2 marqueeStart;
    private Vector2 marqueeEnd;
    
    // Lasso drawing points
    private List<Vector2> lassoPoints = new List<Vector2>();
    public List<Vector2> LassoPoints => lassoPoints;

    // Statistics
    private int[] labelCounts = new int[7]; // Index 0-5 for classes, 6 for deleted/noise
    private bool statsDirty = true;

    // Asynchronous background task execution flags
    private volatile bool finishedConnectionFlag = false;
    private volatile bool finishedRansacFlag = false;
    private volatile bool finishedExportFlag = false;

    // UI Component Reference
    private PointCloudEditorUI editorUI;

    // キャッシュ用配列群 (接続探索の GC Alloc / new 回避用)
    private int[] connQueue = null;

    // CloudCompare風のセル単位接続探索用キャッシュ
    private int[] connCellBucketHead = null;
    private int[] connCellNext = null;
    private int[] connCellX = null;
    private int[] connCellY = null;
    private int[] connCellZ = null;
    private int[] connCellPointHead = null;
    private int[] connPointNextInCell = null;
    private int[] connCellQueue = null;
    private bool[] connCellVisited = null;



    // Properties for UI access
    public bool IsDrawingMarquee => isDrawingMarquee;
    public Vector2 MarqueeStart => marqueeStart;
    public Vector2 MarqueeEnd => marqueeEnd;

    public int[] GetLabelCounts() => labelCounts;
    public void MarkStatsDirty() => statsDirty = true;

    void Start()
    {
        if (targetRenderer == null)
        {
            targetRenderer = GetComponent<PointCloudRenderer>();
        }

        editorUI = GetComponent<PointCloudEditorUI>();
        if (editorUI == null)
        {
            editorUI = gameObject.AddComponent<PointCloudEditorUI>();
        }

        CreateBrushVisual();
        statsDirty = true;
    }

    void CreateBrushVisual()
    {
        // 3D Sphere to represent the brush volume in the scene
        brushVisual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(brushVisual.GetComponent<SphereCollider>()); // No physics interference
        brushVisual.name = "Editor_Brush_Visual";
        
        // Semi-transparent material
        brushMaterial = new Material(Shader.Find("Sprites/Default"));
        brushMaterial.color = brushColor;
        brushVisual.GetComponent<MeshRenderer>().sharedMaterial = brushMaterial;
        
        brushVisual.SetActive(false);
    }

    void Update()
    {
        if (targetRenderer == null) return;

        // Process asynchronous background task completion in main thread
        if (finishedConnectionFlag)
        {
            finishedConnectionFlag = false;
            try
            {
                targetRenderer.UpdatePointBuffer();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PointCloudEditor] UpdatePointBuffer failed: {ex.Message}");
            }
            statsDirty = true;
            PointCloudProgressManager.Instance.Complete();
        }
        if (finishedRansacFlag)
        {
            finishedRansacFlag = false;
            targetRenderer.UpdatePointBuffer();
            statsDirty = true;
            PointCloudProgressManager.Instance.Complete();
        }
        if (finishedExportFlag)
        {
            finishedExportFlag = false;
            PointCloudProgressManager.Instance.Complete();
        }

        // Lock interactions if a background task is running (modal progress dialog)
        if (PointCloudProgressManager.Instance.IsRunning)
        {
            if (brushVisual != null && brushVisual.activeSelf)
            {
                brushVisual.SetActive(false);
            }
            return;
        }

        // Clean up brush visual if tool changed
        if (activeTool != EditTool.Brush && brushVisual.activeSelf)
        {
            brushVisual.SetActive(false);
        }

        // Handle tool interactions
        if (activeTool == EditTool.Brush)
        {
            HandleBrushTool();
        }
        else if (activeTool == EditTool.Marquee)
        {
            HandleMarqueeTool();
        }
        else if (activeTool == EditTool.Lasso)
        {
            HandleLassoTool();
        }
        else if (activeTool == EditTool.Connect)
        {
            HandleConnectTool();
        }

        // Recalculate stats if marked dirty
        if (statsDirty)
        {
            RecalculateStats();
        }
    }

    void HandleBrushTool()
    {
        if (editorUI != null && editorUI.IsMouseOverUI())
        {
            if (brushVisual != null) brushVisual.SetActive(false);
            return;
        }

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Vector3 hitPoint;
        bool hit = FindClosestPointOnRay(ray, out hitPoint);

        if (hit)
        {
            brushVisual.SetActive(true);
            brushVisual.transform.position = hitPoint;
            brushVisual.transform.localScale = Vector3.one * (brushRadius * 2f);

            // Adjust brush radius with Alt + Mouse Scroll
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            {
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    brushRadius = Mathf.Max(0.01f, brushRadius + scroll * 0.2f);
                }
            }

            // Perform selection when Middle Mouse (Wheel press) is dragged
            if (Input.GetMouseButton(2))
            {
                ApplyBrushSelection(hitPoint);
            }
        }
        else
        {
            brushVisual.SetActive(false);
        }
    }

    void HandleMarqueeTool()
    {
        if (editorUI != null && editorUI.IsMouseOverUI() && !isDrawingMarquee)
        {
            return;
        }

        if (Input.GetMouseButtonDown(2))
        {
            isDrawingMarquee = true;
            marqueeStart = Input.mousePosition;
        }

        if (isDrawingMarquee)
        {
            marqueeEnd = Input.mousePosition;

            if (Input.GetMouseButtonUp(2))
            {
                isDrawingMarquee = false;
                ApplyMarqueeSelection();
            }
        }
    }

    // Cache list to avoid GC allocation spikes
    private List<int> searchCandidates = new List<int>();

    // High performance point search under mouse ray using local space transformation & Octree
    bool FindClosestPointOnRay(Ray worldRay, out Vector3 hitWorldPoint)
    {
        hitWorldPoint = Vector3.zero;
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return false;

        Matrix4x4 worldToLocal = targetRenderer.transform.worldToLocalMatrix;
        Vector3 localOrigin = worldToLocal.MultiplyPoint(worldRay.origin);
        Vector3 localDir = worldToLocal.MultiplyVector(worldRay.direction).normalized;
        Ray localRay = new Ray(localOrigin, localDir);

        float pickingThreshold = 0.15f; 
        float localThreshold = pickingThreshold / targetRenderer.transform.lossyScale.x;

        // Search for the point closest to the ray (minimum perpendicular distance)
        float minRayDistSq = float.MaxValue;
        bool found = false;
        Vector3 bestLocalPoint = Vector3.zero;

        // Check if Octree is available and ready
        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady;

        if (useOctree)
        {
            // Traverse Octree recursively to find candidate points close to Ray
            TraverseRay(octree.root, localRay, localThreshold, ref minRayDistSq, ref bestLocalPoint, ref found, points);
        }
        else
        {
            // Fallback to legacy linear search if Octree is still building
            for (int i = 0; i < points.Length; i++)
            {
                if ((points[i].label & 0x20000) != 0) continue; // skip deleted

                Vector3 p = points[i].position;
                Vector3 v = p - localRay.origin;
                float proj = Vector3.Dot(v, localRay.direction);
                if (proj < 0) continue;

                Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
                float distSq = (p - closestPointOnRay).sqrMagnitude;
                if (distSq < localThreshold * localThreshold && distSq < minRayDistSq)
                {
                    minRayDistSq = distSq;
                    bestLocalPoint = p;
                    found = true;
                }
            }
        }

        if (found)
        {
            hitWorldPoint = targetRenderer.transform.TransformPoint(bestLocalPoint);
            return true;
        }
        return false;
    }

    private void TraverseRay(PointCloudOctree.Node node, Ray localRay, float localThreshold, ref float minRayDistSq, ref Vector3 bestLocalPoint, ref bool found, PointData[] points)
    {
        if (node == null) return;

        // Ray vs Sphere check (expanded by localThreshold to avoid edge clipping)
        float distanceProj;
        if (!RaySphereIntersect(localRay, node.center, node.radius + localThreshold, out distanceProj))
        {
            return;
        }

        // Search points in this node
        foreach (int idx in node.pointIndices)
        {
            if ((points[idx].label & 0x20000) != 0) continue;

            Vector3 p = points[idx].position;
            Vector3 v = p - localRay.origin;
            float proj = Vector3.Dot(v, localRay.direction);
            if (proj < 0) continue;

            Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
            float distSq = (p - closestPointOnRay).sqrMagnitude;
            if (distSq < localThreshold * localThreshold && distSq < minRayDistSq)
            {
                minRayDistSq = distSq;
                bestLocalPoint = p;
                found = true;
            }
        }

        // Recursively search children
        if (!node.isLeaf)
        {
            for (int i = 0; i < 8; i++)
            {
                if (node.children[i] != null)
                {
                    TraverseRay(node.children[i], localRay, localThreshold, ref minRayDistSq, ref bestLocalPoint, ref found, points);
                }
            }
        }
    }

    private bool RaySphereIntersect(Ray ray, Vector3 center, float radius, out float distanceProj)
    {
        distanceProj = 0f;
        Vector3 toCenter = center - ray.origin;
        distanceProj = Vector3.Dot(toCenter, ray.direction);

        if (distanceProj < 0)
        {
            // If origin is inside the sphere, it still intersects
            return toCenter.sqrMagnitude <= radius * radius;
        }

        Vector3 closestPoint = ray.origin + ray.direction * distanceProj;
        float distSq = (center - closestPoint).sqrMagnitude;
        return distSq <= radius * radius;
    }

    // Apply brush selection (Multi-threaded Parallel.For with Octree acceleration)
    void ApplyBrushSelection(Vector3 brushCenterWorld)
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        Vector3 localBrushCenter = targetRenderer.transform.InverseTransformPoint(brushCenterWorld);
        float localBrushRadius = brushRadius / targetRenderer.transform.lossyScale.x;
        float radiusSq = localBrushRadius * localBrushRadius;

        bool selecting = brushSelectMode;

        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady;

        if (useOctree)
        {
            searchCandidates.Clear();
            TraverseBrush(octree.root, localBrushCenter, localBrushRadius, searchCandidates);

            Parallel.For(0, searchCandidates.Count, idxInCandidates =>
            {
                int i = searchCandidates[idxInCandidates];
                int label = points[i].label;
                bool isDeleted = (label & 0x20000) != 0;
                if (isDeleted) return;

                float distSq = (points[i].position - localBrushCenter).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    if (selecting) label |= 0x10000;
                    else label &= ~0x10000;
                    points[i].label = label;
                }
            });
        }
        else
        {
            // Fallback to full scanning
            Parallel.For(0, points.Length, i =>
            {
                int label = points[i].label;
                bool isDeleted = (label & 0x20000) != 0;
                if (isDeleted) return;

                float distSq = (points[i].position - localBrushCenter).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    if (selecting) label |= 0x10000;
                    else label &= ~0x10000;
                    points[i].label = label;
                }
            });
        }

        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    private void TraverseBrush(PointCloudOctree.Node node, Vector3 localBrushCenter, float localBrushRadius, List<int> candidates)
    {
        if (node == null) return;

        // Check if node sphere overlaps with brush sphere
        float dist = Vector3.Distance(node.center, localBrushCenter);
        if (dist > node.radius + localBrushRadius)
        {
            return; // No overlap, prune branch
        }

        candidates.AddRange(node.pointIndices);

        if (node.isLeaf) return;

        for (int i = 0; i < 8; i++)
        {
            if (node.children[i] != null)
            {
                TraverseBrush(node.children[i], localBrushCenter, localBrushRadius, candidates);
            }
        }
    }

    // Apply marquee selection (Multi-threaded Parallel.For with Octree acceleration)
    void ApplyMarqueeSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        Vector2 min = Vector2.Min(marqueeStart, marqueeEnd);
        Vector2 max = Vector2.Max(marqueeStart, marqueeEnd);

        Rect selectRect = new Rect(
            min.x / Screen.width,
            min.y / Screen.height,
            (max.x - min.x) / Screen.width,
            (max.y - min.y) / Screen.height
        );

        Matrix4x4 localToScreen = Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix * targetRenderer.transform.localToWorldMatrix;
        bool selecting = brushSelectMode;

        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady;

        if (useOctree)
        {
            searchCandidates.Clear();
            TraverseMarquee(octree.root, localToScreen, selectRect, searchCandidates);

            Parallel.For(0, searchCandidates.Count, idxInCandidates =>
            {
                int i = searchCandidates[idxInCandidates];
                int label = points[i].label;
                bool isDeleted = (label & 0x20000) != 0;
                if (isDeleted) return;

                Vector4 clipPos = localToScreen * new Vector4(points[i].position.x, points[i].position.y, points[i].position.z, 1f);
                if (clipPos.w <= 0.0001f) return;

                Vector3 ndc = new Vector3(clipPos.x / clipPos.w, clipPos.y / clipPos.w, clipPos.z / clipPos.w);
                Vector2 screenPos = new Vector2(ndc.x * 0.5f + 0.5f, ndc.y * 0.5f + 0.5f);

                if (selectRect.Contains(screenPos))
                {
                    if (selecting) label |= 0x10000;
                    else label &= ~0x10000;
                    points[i].label = label;
                }
            });
        }
        else
        {
            // Fallback to full scanning
            Parallel.For(0, points.Length, i =>
            {
                int label = points[i].label;
                bool isDeleted = (label & 0x20000) != 0;
                if (isDeleted) return;

                Vector4 clipPos = localToScreen * new Vector4(points[i].position.x, points[i].position.y, points[i].position.z, 1f);
                if (clipPos.w <= 0.0001f) return;

                Vector3 ndc = new Vector3(clipPos.x / clipPos.w, clipPos.y / clipPos.w, clipPos.z / clipPos.w);
                Vector2 screenPos = new Vector2(ndc.x * 0.5f + 0.5f, ndc.y * 0.5f + 0.5f);

                if (selectRect.Contains(screenPos))
                {
                    if (selecting) label |= 0x10000;
                    else label &= ~0x10000;
                    points[i].label = label;
                }
            });
        }

        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    private void TraverseMarquee(PointCloudOctree.Node node, Matrix4x4 localToScreen, Rect selectRect, List<int> candidates)
    {
        if (node == null) return;

        // Visual screen-space bounding box check to cull entire node
        if (!BoundsOverlapScreenRect(node.bounds, localToScreen, selectRect))
        {
            return;
        }

        candidates.AddRange(node.pointIndices);

        if (node.isLeaf) return;

        for (int i = 0; i < 8; i++)
        {
            if (node.children[i] != null)
            {
                TraverseMarquee(node.children[i], localToScreen, selectRect, candidates);
            }
        }
    }

    private bool BoundsOverlapScreenRect(Bounds bounds, Matrix4x4 localToScreen, Rect selectRect)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        Vector3[] corners = new Vector3[8]
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z)
        };

        float scrMinX = float.MaxValue;
        float scrMaxX = float.MinValue;
        float scrMinY = float.MaxValue;
        float scrMaxY = float.MinValue;

        bool anyInFront = false;

        for (int i = 0; i < 8; i++)
        {
            Vector4 clipPos = localToScreen * new Vector4(corners[i].x, corners[i].y, corners[i].z, 1f);
            if (clipPos.w > 0.0001f)
            {
                anyInFront = true;
                float ndcX = clipPos.x / clipPos.w;
                float ndcY = clipPos.y / clipPos.w;
                float scrX = ndcX * 0.5f + 0.5f;
                float scrY = ndcY * 0.5f + 0.5f;

                if (scrX < scrMinX) scrMinX = scrX;
                if (scrX > scrMaxX) scrMaxX = scrX;
                if (scrY < scrMinY) scrMinY = scrY;
                if (scrY > scrMaxY) scrMaxY = scrY;
            }
        }

        if (!anyInFront) return false;

        Rect boundsScreenRect = Rect.MinMaxRect(scrMinX, scrMinY, scrMaxX, scrMaxY);
        return selectRect.Overlaps(boundsScreenRect);
    }

    // --- GLOBAL EDIT OPERATIONS ---

    public void ClearSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;

        Parallel.For(0, points.Length, i =>
        {
            points[i].label &= ~0x10000;
        });
        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    public void InvertSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;

        Parallel.For(0, points.Length, i =>
        {
            bool isDeleted = (points[i].label & 0x20000) != 0;
            if (!isDeleted)
            {
                points[i].label ^= 0x10000;
            }
        });
        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    public void DeleteSelected()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;

        Parallel.For(0, points.Length, i =>
        {
            bool isSelected = (points[i].label & 0x10000) != 0;
            if (isSelected)
            {
                points[i].label &= ~0x10000;
                points[i].label |= 0x20000;  // Set deleted bit
            }
        });
        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    public void RestoreDeleted()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;

        Parallel.For(0, points.Length, i =>
        {
            points[i].label &= ~0x20000; // Clear deleted bit
        });
        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    public void AssignLabelToSelected()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;

        int classVal = activeLabelClass & 0xFFFF;

        Parallel.For(0, points.Length, i =>
        {
            bool isSelected = (points[i].label & 0x10000) != 0;
            if (isSelected)
            {
                int label = points[i].label;
                label &= ~0xFFFF;        // Clear class ID
                label |= classVal;       // Set class ID
                label &= ~0x10000;       // Clear selected
                points[i].label = label;
            }
        });
        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    // Recalculate statistics for labels
    void RecalculateStats()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;

        System.Array.Clear(labelCounts, 0, labelCounts.Length);

        // Simple single-threaded count (extremely fast loop for simple integer checking)
        for (int i = 0; i < points.Length; i++)
        {
            int labelVal = points[i].label;
            bool isDeleted = (labelVal & 0x20000) != 0;
            if (isDeleted)
            {
                labelCounts[6]++; // Noise/Deleted
            }
            else
            {
                int classId = labelVal & 0xFFFF;
                if (classId >= 0 && classId < 6)
                {
                    labelCounts[classId]++;
                }
            }
        }

        statsDirty = false;
    }

    // Exporter in background thread to prevent freezing
    public void ExportLabeledPoints()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0)
        {
            Debug.LogError("[PointCloudEditor] No points to export!");
            return;
        }

        string inputPath = targetRenderer.GetComponent<PointCloudLoader>().GetFilePath();
        string directory = Path.GetDirectoryName(inputPath);
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
        string exportPath = Path.Combine(directory, $"{fileNameWithoutExt}_labeled.ply");

        var pm = PointCloudProgressManager.Instance;
        pm.Start("PLYファイル書き出し", "書き出しデータ準備中...");

        Debug.Log($"[PointCloudEditor] Starting background export to: {exportPath}");

        Task.Run(() =>
        {
            try
            {
                var token = pm.CancellationToken;
                int nonDeletedCount = 0;
                
                // Count non-deleted points
                for (int i = 0; i < points.Length; i++)
                {
                    if (token.IsCancellationRequested) return;
                    if ((points[i].label & 0x20000) == 0) nonDeletedCount++;
                }

                using (StreamWriter writer = new StreamWriter(exportPath))
                {
                    // PLY ASCII Header
                    writer.WriteLine("ply");
                    writer.WriteLine("format ascii 1.0");
                    writer.WriteLine($"element vertex {nonDeletedCount}");
                    writer.WriteLine("property float x");
                    writer.WriteLine("property float y");
                    writer.WriteLine("property float z");
                    writer.WriteLine("property uchar red");
                    writer.WriteLine("property uchar green");
                    writer.WriteLine("property uchar blue");
                    writer.WriteLine("property int label");
                    writer.WriteLine("end_header");

                    int written = 0;
                    int progressInterval = Mathf.Max(1000, nonDeletedCount / 100);

                    for (int i = 0; i < points.Length; i++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            writer.Close();
                            if (File.Exists(exportPath)) File.Delete(exportPath);
                            return;
                        }

                        int labelVal = points[i].label;
                        bool isDeleted = (labelVal & 0x20000) != 0;
                        if (isDeleted) continue; // skip noise

                        Vector3 pos = points[i].position;
                        Color32 col = PointData.UnpackColor(points[i].originalColor);
                        int classId = labelVal & 0xFFFF;

                        writer.WriteLine($"{pos.x.ToString(CultureInfo.InvariantCulture)} {pos.y.ToString(CultureInfo.InvariantCulture)} {pos.z.ToString(CultureInfo.InvariantCulture)} {col.r} {col.g} {col.b} {classId}");
                        
                        written++;
                        if (written % progressInterval == 0)
                        {
                            pm.Update((float)written / nonDeletedCount, $"データを書き出し中... ({written:N0} / {nonDeletedCount:N0} 点)");
                        }
                    }
                }
                Debug.Log($"[PointCloudEditor] Successfully exported labeled PLY with {nonDeletedCount} points to: {exportPath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PointCloudEditor] Export failed: {ex.Message}");
            }
            finally
            {
                finishedExportFlag = true;
            }
        });
    }

    // ノイズ除去（確定非表示）済みの点群を物理的に除外したPLYファイルを非同期エクスポート
    public void ExportCleanedPoints()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0)
        {
            Debug.LogError("[PointCloudEditor] No points to export!");
            return;
        }

        string inputPath = targetRenderer.GetComponent<PointCloudLoader>().GetFilePath();
        string directory = Path.GetDirectoryName(inputPath);
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
        string exportPath = Path.Combine(directory, $"{fileNameWithoutExt}_cleaned.ply");

        var pm = PointCloudProgressManager.Instance;
        pm.Start("クリーンアップ済PLYエクスポート", "書き出しデータ準備中...");

        Debug.Log($"[PointCloudEditor] Starting background cleaned PLY export to: {exportPath}");

        Task.Run(() =>
        {
            try
            {
                var token = pm.CancellationToken;
                int remainingCount = 0;
                
                // 物理非表示（Deleted or NoiseHidden）以外の有効な点をカウント
                for (int i = 0; i < points.Length; i++)
                {
                    if (token.IsCancellationRequested) return;
                    
                    int labelVal = points[i].label;
                    bool isDeleted = (labelVal & 0x20000) != 0;
                    bool isNoiseHidden = (labelVal & NoiseFilterManager.NOISE_HIDDEN_BIT) != 0;
                    
                    if (!isDeleted && !isNoiseHidden) remainingCount++;
                }

                using (StreamWriter writer = new StreamWriter(exportPath))
                {
                    // PLY ASCII Header
                    writer.WriteLine("ply");
                    writer.WriteLine("format ascii 1.0");
                    writer.WriteLine($"element vertex {remainingCount}");
                    writer.WriteLine("property float x");
                    writer.WriteLine("property float y");
                    writer.WriteLine("property float z");
                    writer.WriteLine("property uchar red");
                    writer.WriteLine("property uchar green");
                    writer.WriteLine("property uchar blue");
                    writer.WriteLine("property int label");
                    writer.WriteLine("end_header");

                    int written = 0;
                    int progressInterval = Mathf.Max(1000, remainingCount / 100);

                    for (int i = 0; i < points.Length; i++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            writer.Close();
                            if (File.Exists(exportPath)) File.Delete(exportPath);
                            return;
                        }

                        int labelVal = points[i].label;
                        bool isDeleted = (labelVal & 0x20000) != 0;
                        bool isNoiseHidden = (labelVal & NoiseFilterManager.NOISE_HIDDEN_BIT) != 0;
                        
                        if (isDeleted || isNoiseHidden) continue; // 物理除外

                        Vector3 pos = points[i].position;
                        Color32 col = PointData.UnpackColor(points[i].originalColor);
                        int classId = labelVal & 0xFFFF; // クラスIDはそのまま書き出す

                        writer.WriteLine($"{pos.x.ToString(CultureInfo.InvariantCulture)} {pos.y.ToString(CultureInfo.InvariantCulture)} {pos.z.ToString(CultureInfo.InvariantCulture)} {col.r} {col.g} {col.b} {classId}");
                        
                        written++;
                        if (written % progressInterval == 0)
                        {
                            pm.Update((float)written / remainingCount, $"データを書き出し中... ({written:N0} / {remainingCount:N0} 点)");
                        }
                    }
                }
                
                // 同時に、Python側が出力した removal_report.json があればエクスポートフォルダにコピーする
                string reportSrc = Path.Combine(Application.dataPath, "../python_backend/output/removal_report.json");
                string reportDest = Path.Combine(directory, $"{fileNameWithoutExt}_removal_report.json");
                if (File.Exists(reportSrc))
                {
                    File.Copy(reportSrc, reportDest, true);
                    Debug.Log($"[PointCloudEditor] Copied removal report to: {reportDest}");
                }

                Debug.Log($"[PointCloudEditor] Successfully exported cleaned PLY with {remainingCount} points to: {exportPath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PointCloudEditor] Cleaned export failed: {ex.Message}");
            }
            finally
            {
                finishedExportFlag = true;
            }
        });
    }

    // --- ADVANCED SELECTION IMPLEMENTATIONS ---

    void HandleLassoTool()
    {
        if (editorUI != null && editorUI.IsMouseOverUI() && lassoPoints.Count == 0)
        {
            if (brushVisual != null) brushVisual.SetActive(false);
            return;
        }

        if (brushVisual != null) brushVisual.SetActive(false);

        // Add vertex on Middle-Click
        if (Input.GetMouseButtonDown(2))
        {
            if (editorUI == null || !editorUI.IsMouseOverUI())
            {
                lassoPoints.Add(Input.mousePosition);
            }
        }

        // Close and apply on Return key or Space key (Right-click removed to avoid camera rotation conflict)
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space))
        {
            if (lassoPoints.Count >= 3)
            {
                ApplyLassoSelection();
            }
            lassoPoints.Clear();
        }
    }

    void ApplyLassoSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        List<Vector2> normalizedPolygon = new List<Vector2>(lassoPoints.Count);
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        foreach (Vector2 p in lassoPoints)
        {
            Vector2 norm = new Vector2(p.x / Screen.width, p.y / Screen.height);
            normalizedPolygon.Add(norm);
            minX = Mathf.Min(minX, norm.x);
            maxX = Mathf.Max(maxX, norm.x);
            minY = Mathf.Min(minY, norm.y);
            maxY = Mathf.Max(maxY, norm.y);
        }

        Rect polygonScreenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);

        Matrix4x4 localToScreen = Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix * targetRenderer.transform.localToWorldMatrix;
        bool selecting = brushSelectMode;

        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady;

        if (useOctree)
        {
            searchCandidates.Clear();
            TraverseMarquee(octree.root, localToScreen, polygonScreenRect, searchCandidates);

            Parallel.For(0, searchCandidates.Count, idxInCandidates =>
            {
                int i = searchCandidates[idxInCandidates];
                int label = points[i].label;
                if ((label & 0x20000) != 0) return;

                Vector4 clipPos = localToScreen * new Vector4(points[i].position.x, points[i].position.y, points[i].position.z, 1f);
                if (clipPos.w <= 0.0001f) return;

                Vector3 ndc = new Vector3(clipPos.x / clipPos.w, clipPos.y / clipPos.w, clipPos.z / clipPos.w);
                Vector2 screenPos = new Vector2(ndc.x * 0.5f + 0.5f, ndc.y * 0.5f + 0.5f);

                if (IsPointInPolygon(screenPos, normalizedPolygon))
                {
                    if (selecting) label |= 0x10000;
                    else label &= ~0x10000;
                    points[i].label = label;
                }
            });
        }
        else
        {
            Parallel.For(0, points.Length, i =>
            {
                int label = points[i].label;
                if ((label & 0x20000) != 0) return;

                Vector4 clipPos = localToScreen * new Vector4(points[i].position.x, points[i].position.y, points[i].position.z, 1f);
                if (clipPos.w <= 0.0001f) return;

                Vector3 ndc = new Vector3(clipPos.x / clipPos.w, clipPos.y / clipPos.w, clipPos.z / clipPos.w);
                Vector2 screenPos = new Vector2(ndc.x * 0.5f + 0.5f, ndc.y * 0.5f + 0.5f);

                if (IsPointInPolygon(screenPos, normalizedPolygon))
                {
                    if (selecting) label |= 0x10000;
                    else label &= ~0x10000;
                    points[i].label = label;
                }
            });
        }

        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    private bool IsPointInPolygon(Vector2 p, List<Vector2> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            if (((polygon[i].y > p.y) != (polygon[j].y > p.y)) &&
                (p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    void HandleConnectTool()
    {
        if (editorUI != null && editorUI.IsMouseOverUI())
        {
            if (brushVisual != null) brushVisual.SetActive(false);
            return;
        }

        if (brushVisual != null) brushVisual.SetActive(false);

        if (Input.GetMouseButtonDown(2))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            Vector3 hitPoint;
            int hitIndex;

            if (FindClosestPointIndexOnRay(ray, out hitIndex, out hitPoint))
            {
                ApplyConnectionSelection(hitIndex);
            }
        }
    }

    bool FindClosestPointIndexOnRay(Ray worldRay, out int hitIndex, out Vector3 hitWorldPoint)
    {
        hitIndex = -1;
        hitWorldPoint = Vector3.zero;
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return false;

        Matrix4x4 worldToLocal = targetRenderer.transform.worldToLocalMatrix;
        Vector3 localOrigin = worldToLocal.MultiplyPoint(worldRay.origin);
        Vector3 localDir = worldToLocal.MultiplyVector(worldRay.direction).normalized;
        Ray localRay = new Ray(localOrigin, localDir);

        float pickingThreshold = 0.15f; 
        float localThreshold = pickingThreshold / targetRenderer.transform.lossyScale.x;

        // Search for the point closest to the ray (minimum perpendicular distance)
        float minRayDistSq = float.MaxValue;
        bool found = false;
        int bestIndex = -1;

        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady;

        if (useOctree)
        {
            TraverseRayIndex(octree.root, localRay, localThreshold, ref minRayDistSq, ref bestIndex, ref found, points);
        }
        else
        {
            for (int i = 0; i < points.Length; i++)
            {
                if ((points[i].label & 0x20000) != 0) continue;

                Vector3 p = points[i].position;
                Vector3 v = p - localRay.origin;
                float proj = Vector3.Dot(v, localRay.direction);
                if (proj < 0) continue;

                Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
                float distSq = (p - closestPointOnRay).sqrMagnitude;
                if (distSq < localThreshold * localThreshold && distSq < minRayDistSq)
                {
                    minRayDistSq = distSq;
                    bestIndex = i;
                    found = true;
                }
            }
        }

        if (found)
        {
            hitIndex = bestIndex;
            hitWorldPoint = targetRenderer.transform.TransformPoint(points[bestIndex].position);
            return true;
        }
        return false;
    }

    private void TraverseRayIndex(PointCloudOctree.Node node, Ray localRay, float localThreshold, ref float minRayDistSq, ref int bestIndex, ref bool found, PointData[] points)
    {
        if (node == null) return;

        float distanceProj;
        if (!RaySphereIntersect(localRay, node.center, node.radius + localThreshold, out distanceProj))
        {
            return;
        }

        foreach (int idx in node.pointIndices)
        {
            if ((points[idx].label & 0x20000) != 0) continue;

            Vector3 p = points[idx].position;
            Vector3 v = p - localRay.origin;
            float proj = Vector3.Dot(v, localRay.direction);
            if (proj < 0) continue;

            Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
            float distSq = (p - closestPointOnRay).sqrMagnitude;
            if (distSq < localThreshold * localThreshold && distSq < minRayDistSq)
            {
                minRayDistSq = distSq;
                bestIndex = idx;
                found = true;
            }
        }

        if (!node.isLeaf)
        {
            for (int i = 0; i < 8; i++)
            {
                if (node.children[i] != null)
                {
                    TraverseRayIndex(node.children[i], localRay, localThreshold, ref minRayDistSq, ref bestIndex, ref found, points);
                }
            }
        }
    }

    void ApplyConnectionSelection(int startIdx)
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        float localRadius = connectionRadius / targetRenderer.transform.lossyScale.x;
        int maxLimit = maxConnectionPoints;
        bool selecting = brushSelectMode;

        Vector3[] positions = targetRenderer.GetPositions();

        var pm = PointCloudProgressManager.Instance;
        pm.Start("空間近接接続探索", "探索開始...");

        Task.Run(() =>
        {
            try
            {
                var token = pm.CancellationToken;
                int numPoints = points.Length;
                int numBuckets = numPoints;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                pm.Update(0f, "セル接続グリッド構築中...");

                float cellSize = localRadius;
                if (cellSize < 0.0001f) cellSize = 0.0001f;
                float invCellSize = 1f / cellSize;

                lock (this)
                {
                    if (connQueue == null || connQueue.Length < numPoints)
                    {
                        connQueue = new int[numPoints];
                    }
                    if (connCellBucketHead == null || connCellBucketHead.Length < numBuckets)
                    {
                        connCellBucketHead = new int[numBuckets];
                    }
                    if (connCellNext == null || connCellNext.Length < numPoints ||
                        connCellX == null || connCellY == null || connCellZ == null ||
                        connCellPointHead == null || connPointNextInCell == null ||
                        connCellQueue == null || connCellVisited == null)
                    {
                        connCellNext = new int[numPoints];
                        connCellX = new int[numPoints];
                        connCellY = new int[numPoints];
                        connCellZ = new int[numPoints];
                        connCellPointHead = new int[numPoints];
                        connPointNextInCell = new int[numPoints];
                        connCellQueue = new int[numPoints];
                        connCellVisited = new bool[numPoints];
                    }
                }

                System.Array.Fill(connCellBucketHead, -1, 0, numBuckets);
                System.Array.Fill(connCellNext, -1, 0, numPoints);
                System.Array.Fill(connCellPointHead, -1, 0, numPoints);
                System.Array.Fill(connPointNextInCell, -1, 0, numPoints);
                System.Array.Clear(connCellVisited, 0, numPoints);

                int cellCount = 0;
                int startCell = -1;

                for (int i = 0; i < numPoints; i++)
                {
                    if (token.IsCancellationRequested) break;
                    if ((points[i].label & 0x20000) != 0) continue;

                    Vector3 pos = positions[i];
                    int vx = Mathf.FloorToInt(pos.x * invCellSize);
                    int vy = Mathf.FloorToInt(pos.y * invCellSize);
                    int vz = Mathf.FloorToInt(pos.z * invCellSize);
                    int h = GetVoxelHash(vx, vy, vz, numBuckets);

                    int cell = connCellBucketHead[h];
                    while (cell != -1)
                    {
                        if (connCellX[cell] == vx && connCellY[cell] == vy && connCellZ[cell] == vz)
                        {
                            break;
                        }
                        cell = connCellNext[cell];
                    }

                    if (cell == -1)
                    {
                        cell = cellCount++;
                        connCellX[cell] = vx;
                        connCellY[cell] = vy;
                        connCellZ[cell] = vz;
                        connCellNext[cell] = connCellBucketHead[h];
                        connCellBucketHead[h] = cell;
                    }

                    connPointNextInCell[i] = connCellPointHead[cell];
                    connCellPointHead[cell] = i;

                    if (i == startIdx)
                    {
                        startCell = cell;
                    }
                }

                if (token.IsCancellationRequested || startCell < 0) return;

                pm.Update(0.1f, "セル接続探索中...");

                int cellHead = 0;
                int cellTail = 0;
                int qTail = 0;
                long visitedCells = 0;
                long lastProgressUpdate = 0;

                connCellQueue[cellTail++] = startCell;
                connCellVisited[startCell] = true;

                while (cellHead < cellTail && qTail < maxLimit)
                {
                    if (token.IsCancellationRequested) break;

                    int cell = connCellQueue[cellHead++];
                    visitedCells++;

                    for (int pointIdx = connCellPointHead[cell]; pointIdx != -1 && qTail < maxLimit; pointIdx = connPointNextInCell[pointIdx])
                    {
                        connQueue[qTail++] = pointIdx;
                    }

                    int cx = connCellX[cell];
                    int cy = connCellY[cell];
                    int cz = connCellZ[cell];

                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;

                        int h = GetVoxelHash(cx + dx, cy + dy, cz + dz, numBuckets);
                        int neighbor = connCellBucketHead[h];
                        while (neighbor != -1)
                        {
                            if (!connCellVisited[neighbor] &&
                                connCellX[neighbor] == cx + dx &&
                                connCellY[neighbor] == cy + dy &&
                                connCellZ[neighbor] == cz + dz)
                            {
                                connCellVisited[neighbor] = true;
                                connCellQueue[cellTail++] = neighbor;
                                break;
                            }
                            neighbor = connCellNext[neighbor];
                        }
                    }

                    long elapsed = sw.ElapsedMilliseconds;
                    if (elapsed - lastProgressUpdate > 100)
                    {
                        lastProgressUpdate = elapsed;
                        float progress = 0.1f + 0.8f * ((float)qTail / maxLimit);
                        pm.Update(progress, $"セル接続探索中... 選択候補: {qTail:N0} / {maxLimit:N0} 点, セル: {visitedCells:N0}");
                    }
                }

                if (!token.IsCancellationRequested && qTail > 0)
                {
                    pm.Update(0.95f, "選択データを点群に適用中...");
                    for (int i = 0; i < qTail; i++)
                    {
                        int idx = connQueue[i];
                        int label = points[idx].label;
                        if (selecting) label |= 0x10000;
                        else label &= ~0x10000;
                        points[idx].label = label;
                    }
                    Debug.Log($"[PointCloudEditor] Cell connection selection completed. Found {qTail} points in {visitedCells} cells. Elapsed: {sw.ElapsedMilliseconds} ms.");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PointCloudEditor] Connection selection failed: {ex.Message}");
            }
            finally
            {
                finishedConnectionFlag = true;
            }
        });
    }

    private static int GetVoxelHash(int x, int y, int z, int numBuckets)
    {
        long hash = ((long)x * 73856093) ^ ((long)y * 19349663) ^ ((long)z * 83492791);
        return (int)((hash & 0x7FFFFFFFFFFFFFFF) % numBuckets);
    }

    public void ApplyRansacSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        Vector3[] positions = targetRenderer.GetPositions();
        if (positions == null || positions.Length == 0) return;

        float localTolerance = ransacTolerance / targetRenderer.transform.lossyScale.x;
        RansacType type = ransacType;
        bool selecting = brushSelectMode;

        var pm = PointCloudProgressManager.Instance;
        pm.Start($"RANSAC検出 ({type})", "点群データを解析中...");

        Task.Run(() =>
        {
            try
            {
                var token = pm.CancellationToken;

                List<int> activeIndices = new List<int>(points.Length / 2);
                for (int i = 0; i < points.Length; i++)
                {
                    if (token.IsCancellationRequested) return;
                    if ((points[i].label & 0x20000) == 0)
                    {
                        activeIndices.Add(i);
                    }
                }

                if (activeIndices.Count < 3)
                {
                    pm.Complete();
                    return;
                }

                object locker = new object();
                object progressLocker = new object();
                int bestInlierCount = 0;
                
                // Keep track of best model parameters instead of huge inlier list to avoid GC allocation spikes
                Vector4 bestPlaneEq = Vector4.zero;
                Vector2 bestCylinderCenter = Vector2.zero;
                float bestCylinderRadius = 0f;

                int iterations = 250;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long lastProgressUpdate = 0;

                if (type == RansacType.Plane)
                {
                    Parallel.For(0, iterations, (iter, state) =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            state.Stop();
                            return;
                        }

                        // Throttle progress updates to avoid lock contention and string allocation bottleneck
                        long elapsed = sw.ElapsedMilliseconds;
                        if (elapsed - lastProgressUpdate > 50)
                        {
                            lock (progressLocker)
                            {
                                if (sw.ElapsedMilliseconds - lastProgressUpdate > 50)
                                {
                                    lastProgressUpdate = sw.ElapsedMilliseconds;
                                    pm.Update((float)iter / iterations, $"平面フィッティング中... (イテレーション {iter}/{iterations})");
                                }
                            }
                        }

                        // Thread-local random source using unique seeds
                        var rand = new System.Random(System.Guid.NewGuid().GetHashCode() + iter);

                        int idx1 = activeIndices[rand.Next(0, activeIndices.Count)];
                        int idx2 = activeIndices[rand.Next(0, activeIndices.Count)];
                        int idx3 = activeIndices[rand.Next(0, activeIndices.Count)];

                        if (idx1 == idx2 || idx2 == idx3 || idx1 == idx3) return;

                        Vector3 p1 = positions[idx1];
                        Vector3 p2 = positions[idx2];
                        Vector3 p3 = positions[idx3];

                        Vector3 normal = Vector3.Cross(p2 - p1, p3 - p1).normalized;
                        if (normal.sqrMagnitude < 0.001f) return;

                        float d = -Vector3.Dot(normal, p1);
                        Vector4 planeEq = new Vector4(normal.x, normal.y, normal.z, d);

                        // Allocation-free inlier counting, cache-friendly vector lookup
                        int currentInlierCount = 0;
                        for (int i = 0; i < activeIndices.Count; i++)
                        {
                            int idx = activeIndices[i];
                            Vector3 p = positions[idx];
                            float dist = Mathf.Abs(planeEq.x * p.x + planeEq.y * p.y + planeEq.z * p.z + planeEq.w);
                            if (dist < localTolerance)
                            {
                                currentInlierCount++;
                            }
                        }

                        lock (locker)
                        {
                            if (currentInlierCount > bestInlierCount)
                            {
                                bestInlierCount = currentInlierCount;
                                bestPlaneEq = planeEq;
                            }
                        }
                    });
                }
                else
                {
                    Parallel.For(0, iterations, (iter, state) =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            state.Stop();
                            return;
                        }

                        long elapsed = sw.ElapsedMilliseconds;
                        if (elapsed - lastProgressUpdate > 50)
                        {
                            lock (progressLocker)
                            {
                                if (sw.ElapsedMilliseconds - lastProgressUpdate > 50)
                                {
                                    lastProgressUpdate = sw.ElapsedMilliseconds;
                                    pm.Update((float)iter / iterations, $"鉛直円柱フィッティング中... (イテレーション {iter}/{iterations})");
                                }
                            }
                        }

                        // Thread-local random source using unique seeds
                        var rand = new System.Random(System.Guid.NewGuid().GetHashCode() + iter);

                        int idx1 = activeIndices[rand.Next(0, activeIndices.Count)];
                        int idx2 = activeIndices[rand.Next(0, activeIndices.Count)];
                        int idx3 = activeIndices[rand.Next(0, activeIndices.Count)];

                        if (idx1 == idx2 || idx2 == idx3 || idx1 == idx3) return;

                        Vector3 p1 = positions[idx1];
                        Vector3 p2 = positions[idx2];
                        Vector3 p3 = positions[idx3];

                        Vector2 a = new Vector2(p1.x, p1.z);
                        Vector2 b = new Vector2(p2.x, p2.z);
                        Vector2 c = new Vector2(p3.x, p3.z);

                        float dVal = 2f * (a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y));
                        if (Mathf.Abs(dVal) < 0.0001f) return;

                        float xc = ((a.x * a.x + a.y * a.y) * (b.y - c.y) + (b.x * b.x + b.y * b.y) * (c.y - a.y) + (c.x * c.x + c.y * c.y) * (a.y - b.y)) / dVal;
                        float zc = ((a.x * a.x + a.y * a.y) * (c.x - b.x) + (b.x * b.x + b.y * b.y) * (a.x - c.x) + (c.x * c.x + c.y * c.y) * (b.x - a.x)) / dVal;

                        Vector2 center = new Vector2(xc, zc);
                        float radius = Vector2.Distance(a, center);

                        if (radius > 0.08f) return; //トマト支柱の想定半径を超えたら棄却

                        // Allocation-free inlier counting, cache-friendly vector lookup
                        int currentInlierCount = 0;
                        for (int i = 0; i < activeIndices.Count; i++)
                        {
                            int idx = activeIndices[i];
                            Vector3 p = positions[idx];
                            float distFromCenter = Vector2.Distance(new Vector2(p.x, p.z), center);
                            float error = Mathf.Abs(distFromCenter - radius);

                            if (error < localTolerance)
                            {
                                currentInlierCount++;
                            }
                        }

                        lock (locker)
                        {
                            if (currentInlierCount > bestInlierCount)
                            {
                                bestInlierCount = currentInlierCount;
                                bestCylinderCenter = center;
                                bestCylinderRadius = radius;
                            }
                        }
                    });
                }

                if (!token.IsCancellationRequested && bestInlierCount > 0)
                {
                    pm.Update(0.95f, "適合データを点群に適用中...");
                    
                    // Final extraction of best model's inliers in parallel
                    Parallel.For(0, activeIndices.Count, i =>
                    {
                        int idx = activeIndices[i];
                        Vector3 p = positions[idx];
                        float dist = 0f;

                        if (type == RansacType.Plane)
                        {
                            dist = Mathf.Abs(bestPlaneEq.x * p.x + bestPlaneEq.y * p.y + bestPlaneEq.z * p.z + bestPlaneEq.w);
                        }
                        else
                        {
                            float distFromCenter = Vector2.Distance(new Vector2(p.x, p.z), bestCylinderCenter);
                            dist = Mathf.Abs(distFromCenter - bestCylinderRadius);
                        }

                        if (dist < localTolerance)
                        {
                            int label = points[idx].label;
                            if (selecting) label |= 0x10000;
                            else label &= ~0x10000;
                            points[idx].label = label;
                        }
                    });
                    Debug.Log($"[RANSAC] Finished RANSAC detection ({type}). Fitted {bestInlierCount} points.");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PointCloudEditor] RANSAC failed: {ex.Message}");
            }
            finally
            {
                finishedRansacFlag = true;
            }
        });
    }

    public void ApplyAttributeFilterSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        bool selecting = brushSelectMode;

        Parallel.For(0, points.Length, i =>
        {
            int label = points[i].label;
            if ((label & 0x20000) != 0) return;

            bool pass = false;
            float val = 0f;

            if (filterType == FilterType.Height)
            {
                val = points[i].position.y;
                pass = (val >= filterMin && val <= filterMax);
            }
            else if (filterType == FilterType.Distance)
            {
                val = points[i].distance;
                pass = (val >= filterMin && val <= filterMax);
            }
            else if (filterType == FilterType.Redness)
            {
                Color32 c = PointData.UnpackColor(points[i].originalColor);
                val = (float)c.r / Mathf.Max(1f, (float)c.g + c.b);
                pass = (val >= filterMin && val <= filterMax);
            }
            else if (filterType == FilterType.Greenness)
            {
                Color32 c = PointData.UnpackColor(points[i].originalColor);
                val = (float)c.g / Mathf.Max(1f, (float)c.r + c.b);
                pass = (val >= filterMin && val <= filterMax);
            }

            if (pass)
            {
                if (selecting) label |= 0x10000;
                else label &= ~0x10000;
                points[i].label = label;
            }
        });

        targetRenderer.UpdatePointBuffer();
        statsDirty = true;
    }

    void OnDestroy()
    {
        if (brushVisual != null) Destroy(brushVisual);
        if (brushMaterial != null) Destroy(brushMaterial);
    }
}
