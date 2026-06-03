using UnityEngine;
using PointCloudWorkbench;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public class PointCloudRenderer : MonoBehaviour
{
    [Header("Rendering Settings")]
    public Shader pointShader;
    public float pointSize = 2.0f;

    [Header("Scalar Fields Mode")]
    [Range(0, 3)]
    public int colorMode = 0; // 0: RGB, 1: Height, 2: Label, 3: Distance
    public float minHeight = -2f;
    public float maxHeight = 2f;
    public float maxDistanceThreshold = 1f;

    [Header("LOD & Culling Settings")]
    public bool enableLOD = true;
    [Range(0.005f, 0.1f)]
    public float lodThreshold = 0.02f;
    public int maxPointsPerNode = 1024;
    public int maxOctreeDepth = 8;

    // Point cloud data
    private PointData[] pointData;
    private ComputeBuffer pointBuffer;
    private Material pointMaterial;
    private Bounds localBounds;
    private bool isInitialized = false;

    // Dynamic label colors
    private Vector4[] labelColors = new Vector4[64];

    // Annotation layers
    private Dictionary<string, byte[]> annotationLayers = new Dictionary<string, byte[]>();
    private string activeAnnotationLayer = "Default";

    // Cache arrays to support legacy C# scripts accessing positions directly
    private Vector3[] cachedPositions;
    private Color[] cachedColors;

    // LOD & Culling buffers and structures
    private PointCloudOctree octree;
    private ComputeBuffer drawIndexBuffer;
    private ComputeBuffer fullIndexBuffer;
    private List<int> visibleIndices = new List<int>();
    private bool isOctreeBuilding = false;
    private bool isOctreeReady = false;
    private int activeDrawCount = 0;

    // Lock and variables for background construction thread
    private readonly object octreeLock = new object();
    private PointCloudOctree pendingOctree;
    private bool hasPendingOctree = false;

    void Awake()
    {
        // 謎のパーティクルを消すため、同じGameObjectにあるParticleSystemとParticleSystemRendererを破壊する
        var ps = GetComponent<ParticleSystem>();
        if (ps != null)
        {
            DestroyImmediate(ps);
            Debug.Log("[PointCloudRenderer] Removed obsolete ParticleSystem component to stop stray particles.");
        }
        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr != null)
        {
            DestroyImmediate(psr);
            Debug.Log("[PointCloudRenderer] Removed obsolete ParticleSystemRenderer component.");
        }
    }

    void Start()
    {
        Initialize();
        if (pointData == null || pointData.Length == 0)
        {
            GenerateDemoPointCloud();
        }
    }

    public void Initialize()
    {
        if (isInitialized && pointMaterial != null) return;

        // Automatically convert legacy meter-based point size (e.g. 0.003f) to pixel-based (e.g. 2.0f)
        if (pointSize < 0.5f)
        {
            pointSize = 2.0f;
            Debug.Log($"[PointCloudRenderer] Upgraded legacy point size to pixel-based default (2.0px).");
        }

        // Force resolution of the shader by name to bypass any stale serialized shader references in Inspector
        var resolvedShader = Shader.Find("PointCloudWorkbench/PointCloudShader");
        if (resolvedShader != null)
        {
            pointShader = resolvedShader;
        }
        else if (pointShader == null)
        {
            Debug.LogError("[PointCloudRenderer] PointCloudShader not found! Add it to Always Included Shaders in Graphics Settings.");
            return;
        }

        pointMaterial = new Material(pointShader);
        InitializeDefaultLabelColors();
        isInitialized = true;
        Debug.Log("[PointCloudRenderer] Initialized with shader: " + pointShader.name);
    }

    private void InitializeDefaultLabelColors()
    {
        // Initialize default dynamic label colors (0-6 matching original shader fallback)
        for (int i = 0; i < 64; i++)
        {
            labelColors[i] = new Vector4(0.5f, 0.5f, 0.5f, 1.0f); // Default grey
        }
        labelColors[0] = new Vector4(0.7f, 0.7f, 0.7f, 1.0f); // Unclassified (Light Grey)
        labelColors[1] = new Vector4(0.55f, 0.35f, 0.15f, 1.0f); // Stem (Brown)
        labelColors[2] = new Vector4(0.1f, 0.7f, 0.2f, 1.0f); // Leaf (Green)
        labelColors[3] = new Vector4(1.0f, 0.1f, 0.1f, 1.0f); // Fruit (Red)
        labelColors[4] = new Vector4(1.0f, 0.9f, 0.0f, 1.0f); // Flower (Yellow)
        labelColors[5] = new Vector4(0.0f, 0.6f, 0.9f, 1.0f); // Support (Cyan/Blue)
        labelColors[6] = new Vector4(0.9f, 0.0f, 0.9f, 1.0f); // Noise (Magenta)
    }

    public void SetLabelColors(Vector4[] colors)
    {
        if (colors == null) return;
        int count = Mathf.Min(colors.Length, labelColors.Length);
        for (int i = 0; i < count; i++)
        {
            labelColors[i] = colors[i];
        }

        if (pointMaterial != null)
        {
            pointMaterial.SetVectorArray("_LabelColors", labelColors);
        }
    }

    public void InitializeAnnotationLayers(int pointCount)
    {
        annotationLayers.Clear();
        byte[] defaultLabels = new byte[pointCount];

        if (pointData != null && pointData.Length == pointCount)
        {
            for (int i = 0; i < pointCount; i++)
            {
                defaultLabels[i] = (byte)(pointData[i].label & 0xFF);
            }
        }

        annotationLayers["Default"] = defaultLabels;
        activeAnnotationLayer = "Default";
    }

    public void AddAnnotationLayer(string layerName)
    {
        if (pointData == null) return;
        if (!annotationLayers.ContainsKey(layerName))
        {
            annotationLayers[layerName] = new byte[pointData.Length];
        }
    }

    public void DeleteAnnotationLayer(string layerName)
    {
        if (layerName == "Default") return;
        if (annotationLayers.ContainsKey(layerName))
        {
            annotationLayers.Remove(layerName);
            if (activeAnnotationLayer == layerName)
            {
                SwitchAnnotationLayer("Default");
            }
        }
    }

    public void SwitchAnnotationLayer(string newLayerName)
    {
        if (pointData == null) return;
        int count = pointData.Length;

        // Save current labels
        if (annotationLayers.ContainsKey(activeAnnotationLayer))
        {
            byte[] currentLabels = annotationLayers[activeAnnotationLayer];
            if (currentLabels.Length == count)
            {
                for (int i = 0; i < count; i++)
                {
                    currentLabels[i] = (byte)(pointData[i].label & 0xFF);
                }
            }
        }

        // Auto initialize new layer if it doesn't exist
        if (!annotationLayers.ContainsKey(newLayerName))
        {
            annotationLayers[newLayerName] = new byte[count];
        }

        // Apply new labels
        byte[] targetLabels = annotationLayers[newLayerName];
        for (int i = 0; i < count; i++)
        {
            int labelVal = pointData[i].label;
            labelVal &= ~0xFF;
            labelVal |= targetLabels[i];
            pointData[i].label = labelVal;
        }

        activeAnnotationLayer = newLayerName;
        UpdatePointBuffer();
    }

    public List<string> GetAnnotationLayerNames()
    {
        return new List<string>(annotationLayers.Keys);
    }

    public string GetActiveAnnotationLayerName()
    {
        return activeAnnotationLayer;
    }

    public Dictionary<string, byte[]> GetAnnotationLayers()
    {
        return annotationLayers;
    }

    // Set dynamic points from standard positions and colors (used by PointCloudLoader)
    public void SetPointCloudData(Vector3[] positions, Color[] colors)
    {
        Initialize();

        int count = positions.Length;
        pointData = new PointData[count];
        cachedPositions = positions;
        
        if (colors != null)
        {
            cachedColors = colors;
        }
        else
        {
            cachedColors = new Color[count];
            for (int i = 0; i < count; i++) cachedColors[i] = Color.white;
        }

        // Compute local bounds
        Vector3 min = count > 0 ? positions[0] : Vector3.zero;
        Vector3 max = count > 0 ? positions[0] : Vector3.zero;

        for (int i = 0; i < count; i++)
        {
            Color32 c32 = cachedColors[i];
            pointData[i] = new PointData(positions[i], c32, 0, 0f);

            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }

        // Bounds size should cover the points + extra room for size
        localBounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 0.5f);

        // Recreate Compute Buffer
        RecreateComputeBuffer(count);
        pointBuffer.SetData(pointData);

        // Start background octree construction
        StartOctreeBuild(positions);

        InitializeAnnotationLayers(count);

        Debug.Log($"[PointCloudRenderer] ComputeBuffer initialized with {count} points.");
    }

    // High performance SetData with full struct (for internal workbench use)
    public void SetPointCloudData(PointData[] data)
    {
        Initialize();
        pointData = data;
        int count = data.Length;

        cachedPositions = new Vector3[count];
        cachedColors = new Color[count];

        Vector3 min = count > 0 ? data[0].position : Vector3.zero;
        Vector3 max = count > 0 ? data[0].position : Vector3.zero;

        for (int i = 0; i < count; i++)
        {
            cachedPositions[i] = data[i].position;
            cachedColors[i] = PointData.UnpackColor(data[i].originalColor);

            min = Vector3.Min(min, data[i].position);
            max = Vector3.Max(max, data[i].position);
        }

        localBounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 0.5f);

        RecreateComputeBuffer(count);
        pointBuffer.SetData(pointData);

        // Start background octree construction
        StartOctreeBuild(cachedPositions);

        InitializeAnnotationLayers(count);
    }

    private void RecreateComputeBuffer(int count)
    {
        if (pointBuffer != null)
        {
            pointBuffer.Release();
            pointBuffer = null;
        }

        if (count > 0)
        {
            // PointData size: float3(12) + uint(4) + int(4) + float(4) = 24 bytes
            pointBuffer = new ComputeBuffer(count, Marshal.SizeOf(typeof(PointData)));
        }
    }

    private void StartOctreeBuild(Vector3[] positions)
    {
        lock (octreeLock)
        {
            isOctreeReady = false;
            isOctreeBuilding = true;
            hasPendingOctree = false;
            pendingOctree = null;
        }

        // Recreate flat index buffer for fallback rendering during build
        RecreateFullIndexBuffer(positions.Length);

        int maxPoints = maxPointsPerNode;
        int maxDepth = maxOctreeDepth;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var newOctree = new PointCloudOctree();
                newOctree.Build(positions, maxPoints, maxDepth);

                lock (octreeLock)
                {
                    pendingOctree = newOctree;
                    hasPendingOctree = true;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PointCloudRenderer] Failed to build Octree in background: {ex.Message}");
            }
        });
    }

    private void RecreateFullIndexBuffer(int count)
    {
        if (fullIndexBuffer != null)
        {
            fullIndexBuffer.Release();
            fullIndexBuffer = null;
        }

        if (count > 0)
        {
            int[] indices = new int[count];
            for (int i = 0; i < count; i++) indices[i] = i;
            fullIndexBuffer = new ComputeBuffer(count, sizeof(int));
            fullIndexBuffer.SetData(indices);
        }
    }

    void Update()
    {
        // Check if background octree building task is completed
        if (isOctreeBuilding && hasPendingOctree)
        {
            lock (octreeLock)
            {
                if (hasPendingOctree)
                {
                    octree = pendingOctree;
                    pendingOctree = null;
                    hasPendingOctree = false;
                    isOctreeBuilding = false;
                    isOctreeReady = true;

                    if (drawIndexBuffer != null)
                    {
                        drawIndexBuffer.Release();
                        drawIndexBuffer = null;
                    }
                    drawIndexBuffer = new ComputeBuffer(pointData.Length, sizeof(int));

                    Debug.Log($"[PointCloudRenderer] Octree ready for LOD rendering. Node Count: {CountNodes(octree.root)}");
                }
            }
        }
    }

    private int CountNodes(PointCloudOctree.Node node)
    {
        if (node == null) return 0;
        int count = 1;
        if (!node.isLeaf)
        {
            for (int i = 0; i < 8; i++)
            {
                count += CountNodes(node.children[i]);
            }
        }
        return count;
    }

    public void UpdatePointBuffer()
    {
        if (pointBuffer != null && pointData != null)
        {
            pointBuffer.SetData(pointData);

            // DEBUG CHECK
            int nonZeroCount = 0;
            int candidateCount = 0;
            int hiddenCount = 0;
            for (int i = 0; i < pointData.Length; i++)
            {
                if (pointData[i].label != 0)
                {
                    nonZeroCount++;
                    if ((pointData[i].label & NoiseFilterManager.NOISE_CANDIDATE_BIT) != 0) candidateCount++;
                    if ((pointData[i].label & NoiseFilterManager.NOISE_HIDDEN_BIT) != 0) hiddenCount++;
                }
            }
            Debug.Log($"[PointCloudRenderer] UpdatePointBuffer executed. Total points: {pointData.Length}, Non-zero label points: {nonZeroCount}, Candidates: {candidateCount}, Hidden: {hiddenCount}");

            // Verify GPU buffer by reading back first few points
            try
            {
                int testLength = Mathf.Min(100, pointData.Length);
                PointData[] readBack = new PointData[testLength];
                pointBuffer.GetData(readBack, 0, 0, testLength);
                int gpuNonZeroCount = 0;
                for (int i = 0; i < testLength; i++)
                {
                    if (readBack[i].label != 0) gpuNonZeroCount++;
                }
                Debug.Log($"[PointCloudRenderer] GPU Buffer Readback Test: First {testLength} points have {gpuNonZeroCount} non-zero labels. (C# original non-zero in same range: {gpuNonZeroCount})");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PointCloudRenderer] Failed to read back GPU buffer: {ex.Message}");
            }
        }
        else
        {
            Debug.LogWarning($"[PointCloudRenderer] UpdatePointBuffer skipped: pointBuffer is null = {pointBuffer == null}, pointData is null = {pointData == null}");
        }
    }

    public PointData[] GetPointData()
    {
        return pointData;
    }

    public int GetActiveDrawCount()
    {
        return activeDrawCount;
    }

    public bool IsOctreeBuilding => isOctreeBuilding;
    public bool IsOctreeReady => isOctreeReady;
    public PointCloudOctree Octree => octree;

    // Required by PointCloudManager
    public Vector3[] GetPositions()
    {
        if (cachedPositions == null && pointData != null)
        {
            cachedPositions = new Vector3[pointData.Length];
            for (int i = 0; i < pointData.Length; i++)
            {
                cachedPositions[i] = pointData[i].position;
            }
        }
        return cachedPositions;
    }

    public void SetPointSize(float size)
    {
        pointSize = size;
    }

    public void ShowOriginalColors()
    {
        colorMode = 0;
    }

    public void ShowHeightMap(float minH, float maxH)
    {
        colorMode = 1;
        minHeight = minH;
        maxHeight = maxH;
    }

    public void ShowDistanceMap(float[] distances, float maxDistThreshold)
    {
        colorMode = 3;
        maxDistanceThreshold = maxDistThreshold;

        if (pointData == null || distances == null) return;

        int count = Mathf.Min(pointData.Length, distances.Length);
        for (int i = 0; i < count; i++)
        {
            pointData[i].distance = distances[i];
        }

        UpdatePointBuffer();
    }

    // Set labels (used in manual annotation)
    public void SetLabels(int[] labels)
    {
        if (pointData == null || labels == null) return;
        int count = Mathf.Min(pointData.Length, labels.Length);
        for (int i = 0; i < count; i++)
        {
            pointData[i].label = labels[i];
        }
        UpdatePointBuffer();
    }

    public void GenerateDemoPointCloud()
    {
        int count = 50000;
        Vector3[] pos = new Vector3[count];
        Color[] col = new Color[count];
        for (int i = 0; i < count; i++)
        {
            float x = Random.Range(-2f, 2f);
            float z = Random.Range(-2f, 2f);
            float distance = Mathf.Sqrt(x * x + z * z);
            float y = Mathf.Sin(distance * 4f) * 0.3f + 1.0f;
            pos[i] = new Vector3(x, y, z);
            col[i] = Color.Lerp(Color.green, Color.red, distance / 3f);
        }
        SetPointCloudData(pos, col);
    }

    void LateUpdate()
    {
        // Re-initialize if material was lost
        if (pointMaterial == null)
        {
            isInitialized = false;
            Initialize();
        }

        if (pointBuffer == null || pointMaterial == null || pointData == null || pointData.Length == 0)
        {
            return;
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
#if UNITY_2023_1_OR_NEWER
            cam = FindAnyObjectByType<Camera>();
#else
            cam = FindObjectOfType<Camera>();
#endif
        }
        if (cam == null) cam = Camera.current;

        bool useLOD = enableLOD && isOctreeReady && cam != null;
        int drawCount = pointData.Length;

        if (useLOD)
        {
            visibleIndices.Clear();
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            Vector3 camPos = cam.transform.position;
            float scale = Mathf.Max(transform.lossyScale.x, Mathf.Max(transform.lossyScale.y, transform.lossyScale.z));

            TraverseOctree(octree.root, planes, camPos, scale, lodThreshold, visibleIndices);
            drawCount = visibleIndices.Count;

            if (drawCount > 0)
            {
                drawIndexBuffer.SetData(visibleIndices);
            }
        }

        // Set shader parameters
        pointMaterial.SetBuffer("_PointBuffer", pointBuffer);
        pointMaterial.SetVectorArray("_LabelColors", labelColors);

        if (useLOD && drawCount > 0)
        {
            pointMaterial.SetBuffer("_Indices", drawIndexBuffer);
        }
        else
        {
            if (fullIndexBuffer == null)
            {
                RecreateFullIndexBuffer(pointData.Length);
            }
            pointMaterial.SetBuffer("_Indices", fullIndexBuffer);
            drawCount = pointData.Length;
        }

        pointMaterial.SetFloat("_PointSize", pointSize);
        pointMaterial.SetInt("_ColorMode", colorMode);
        pointMaterial.SetFloat("_MinHeight", minHeight);
        pointMaterial.SetFloat("_MaxHeight", maxHeight);
        pointMaterial.SetFloat("_MaxDistanceThreshold", maxDistanceThreshold);
        pointMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);

        // Transform local bounds to world space for camera culling
        Bounds worldBounds = new Bounds(
            transform.TransformPoint(localBounds.center), 
            Vector3.Scale(localBounds.size, transform.lossyScale)
        );

        activeDrawCount = drawCount;
        if (drawCount > 0)
        {
            // Graphics.DrawProcedural renders directly. Triangles, 6 vertices per point (1 quad)
            Graphics.DrawProcedural(pointMaterial, worldBounds, MeshTopology.Triangles, drawCount * 6);
        }
    }

    private void TraverseOctree(PointCloudOctree.Node node, Plane[] planes, Vector3 camPos, float scale, float currentThreshold, List<int> outIndices)
    {
        if (node == null) return;

        // 1. Transform bounds sphere to world space
        Vector3 worldCenter = transform.TransformPoint(node.center);
        float worldRadius = node.radius * scale;

        // 2. Frustum culling check
        if (!SphereInFrustum(worldCenter, worldRadius, planes))
        {
            return;
        }

        // 3. Add point indices of current node
        outIndices.AddRange(node.pointIndices);

        if (node.isLeaf) return;

        // 4. LOD threshold evaluation based on distance
        float dist = Vector3.Distance(camPos, worldCenter);
        if (dist < 0.001f) dist = 0.001f;

        float screenSpaceSize = worldRadius / dist;

        // Stop traversal if screen space size is smaller than lodThreshold
        if (screenSpaceSize < currentThreshold)
        {
            return;
        }

        // 5. Recursively traverse children
        for (int i = 0; i < 8; i++)
        {
            if (node.children[i] != null)
            {
                TraverseOctree(node.children[i], planes, camPos, scale, currentThreshold, outIndices);
            }
        }
    }

    private bool SphereInFrustum(Vector3 center, float radius, Plane[] planes)
    {
        for (int i = 0; i < 6; i++)
        {
            if (planes[i].GetDistanceToPoint(center) < -radius)
            {
                return false;
            }
        }
        return true;
    }

    void OnDisable()
    {
        ReleaseBuffers();
    }

    void OnDestroy()
    {
        ReleaseBuffers();

        if (pointMaterial != null)
        {
            Destroy(pointMaterial);
        }
    }

    private void ReleaseBuffers()
    {
        if (pointBuffer != null)
        {
            pointBuffer.Release();
            pointBuffer = null;
        }

        if (drawIndexBuffer != null)
        {
            drawIndexBuffer.Release();
            drawIndexBuffer = null;
        }

        if (fullIndexBuffer != null)
        {
            fullIndexBuffer.Release();
            fullIndexBuffer = null;
        }
    }
}
