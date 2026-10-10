using UnityEngine;
using PointCloudWorkbench;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;

public class PointCloudRenderer : MonoBehaviour
{
    [Header("Rendering Settings")]
    public Shader pointShader;
    public float pointSize = 2.0f;

    [Header("Coordinate Display")]
    [Min(0.001f)]
    public float pointCloudDisplayScale = 1200f;

    [Header("Scalar Fields Mode")]
    [Range(0, 3)]
    public int colorMode = 0; // 0: RGB, 1: Height, 2: Label, 3: Distance
    [Tooltip("Height-map limits in millimeters; point coordinates remain in data-space.")]
    public float minHeight = -2000f;
    [Tooltip("Height-map limits in millimeters; point coordinates remain in data-space.")]
    public float maxHeight = 2000f;
    [Tooltip("C2C distance threshold in millimeters. PointData.distance also stores millimeters.")]
    public float maxDistanceThreshold = 1000f;

    [Header("LOD & Culling Settings")]
    public bool enableLOD = true;
    [Range(0.005f, 0.1f)]
    public float lodThreshold = 0.02f;
    public int maxPointsPerNode = 1024;
    public int maxOctreeDepth = 8;

    // Point cloud data
    private PointData[] pointData;
    private long datasetGeneration;
    private long contentRevision;
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
    private System.Exception pendingOctreeError;
    private bool hasPendingOctree = false;
    private Transform displayTransform;

    public Transform DisplayTransform
    {
        get
        {
            EnsureDisplayTransform();
            return displayTransform;
        }
    }

    public float DisplayScale => Mathf.Max(0.001f, pointCloudDisplayScale);
    public long DatasetGeneration => Interlocked.Read(ref datasetGeneration);
    public long ContentRevision => Interlocked.Read(ref contentRevision);

    // Measurement results interpret one data-space unit as DisplayScale millimeters.
    public float DataLengthToMillimeters(float dataLength)
    {
        return dataLength * DisplayScale;
    }

    public Vector3 DataPointToMillimeters(Vector3 dataPoint)
    {
        return dataPoint * DisplayScale;
    }

    public float MillimetersToDataLength(float lengthMillimeters)
    {
        return lengthMillimeters / DisplayScale;
    }

    public Vector3 MillimetersToDataPoint(Vector3 pointMillimeters)
    {
        return pointMillimeters / DisplayScale;
    }

    void Awake()
    {
        EnsureDisplayTransform();

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

    private void EnsureDisplayTransform()
    {
        // Keep the display-only scale off PointData and the renderer root used by data-space operations.
        if (displayTransform == null)
        {
            displayTransform = transform.Find("PointCloudDisplayScale");
            if (displayTransform == null)
            {
                GameObject displayObject = new GameObject("PointCloudDisplayScale");
                displayTransform = displayObject.transform;
                displayTransform.SetParent(transform, false);
            }
        }

        displayTransform.localPosition = Vector3.zero;
        displayTransform.localRotation = Quaternion.identity;
        displayTransform.localScale = Vector3.one * DisplayScale;
    }

    void Start()
    {
        if (!Initialize()) return;
        if (pointData == null || pointData.Length == 0)
        {
            GenerateDemoPointCloud();
        }
    }

    public bool Initialize()
    {
        if (isInitialized && pointMaterial != null) return true;

        if (SystemInfo.graphicsShaderLevel < 50)
        {
            string message = $"点群表示にはShader Model 5.0以上が必要です。現在のgraphicsShaderLevel={SystemInfo.graphicsShaderLevel}です。";
            HardwareCompatibilityDiagnostic.ReportGraphicsFailure(message);
            Debug.LogError("[PointCloudRenderer] " + message);
            return false;
        }

        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
        {
            const string message = "有効なGraphics API/描画デバイスがありません。Direct3D 11/12またはVulkanを有効にしてください。";
            HardwareCompatibilityDiagnostic.ReportGraphicsFailure(message);
            Debug.LogError("[PointCloudRenderer] " + message);
            return false;
        }

        int pointDataStride = Marshal.SizeOf(typeof(PointData));
        if (pointDataStride != 24)
        {
            string message = $"点群GPUデータ形式が一致しません。PointData={pointDataStride} bytes、必要値=24 bytesです。";
            HardwareCompatibilityDiagnostic.ReportGraphicsFailure(message);
            Debug.LogError("[PointCloudRenderer] " + message);
            return false;
        }

        // Upgrade old sub-pixel point-size settings to the current pixel-based size.
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
            const string message = "必須の点群シェーダーPointCloudWorkbench/PointCloudShaderが見つかりません。";
            HardwareCompatibilityDiagnostic.ReportGraphicsFailure(message);
            Debug.LogError("[PointCloudRenderer] " + message);
            return false;
        }

        if (!pointShader.isSupported)
        {
            string message = $"必須シェーダーが現在のGPU/APIで利用できません: {pointShader.name}";
            HardwareCompatibilityDiagnostic.ReportGraphicsFailure(message);
            Debug.LogError("[PointCloudRenderer] " + message);
            return false;
        }

        pointMaterial = new Material(pointShader);
        InitializeDefaultLabelColors();
        isInitialized = true;
        Debug.Log("[PointCloudRenderer] Initialized with shader: " + pointShader.name);
        return true;
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
        if (!Initialize()) throw new System.InvalidOperationException("GPU描画条件を満たさないため、点群を設定できません。");
        if (positions == null || positions.Length == 0)
            throw new System.ArgumentException("点群データが空です。", nameof(positions));

        int count = positions.Length;
        if (colors != null && colors.Length != count)
            throw new System.ArgumentException("座標と色の点数が一致しません。", nameof(colors));

        PointData[] nextPointData = new PointData[count];
        Color[] nextCachedColors = colors ?? new Color[count];
        if (colors != null)
        {
            nextCachedColors = colors;
        }
        else
        {
            for (int i = 0; i < count; i++) nextCachedColors[i] = Color.white;
        }

        Vector3 min = count > 0 ? positions[0] : Vector3.zero;
        Vector3 max = count > 0 ? positions[0] : Vector3.zero;

        for (int i = 0; i < count; i++)
        {
            Color32 c32 = nextCachedColors[i];
            nextPointData[i] = new PointData(positions[i], c32, 0, 0f);

            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }

        Bounds nextBounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 0.5f);
        Dictionary<string, byte[]> nextAnnotationLayers = CreateAnnotationLayers(nextPointData);
        ComputeBuffer nextPointBuffer = CreatePointBuffer(nextPointData);
        ComputeBuffer nextFullIndexBuffer;
        try { nextFullIndexBuffer = CreateFullIndexBuffer(count); }
        catch { ReleaseBufferSafely(nextPointBuffer); throw; }

        ComputeBuffer previousPointBuffer = pointBuffer;
        ComputeBuffer previousFullIndexBuffer = fullIndexBuffer;
        pointData = nextPointData;
        cachedPositions = positions;
        cachedColors = nextCachedColors;
        localBounds = nextBounds;
        pointBuffer = nextPointBuffer;
        fullIndexBuffer = nextFullIndexBuffer;
        annotationLayers = nextAnnotationLayers;
        activeAnnotationLayer = "Default";
        Interlocked.Increment(ref datasetGeneration);
        Interlocked.Increment(ref contentRevision);
        ReleaseBufferSafely(previousPointBuffer);
        ReleaseBufferSafely(previousFullIndexBuffer);

        try { StartOctreeBuild(positions, fallbackIndexBufferAlreadyStaged: true); }
        catch (System.Exception ex) { UseFullPointListFallback(ex); }

        Debug.Log($"[PointCloudRenderer] ComputeBuffer initialized with {count} points.");
    }

    // High performance SetData with full struct (for internal workbench use)
    public void SetPointCloudData(PointData[] data)
    {
        if (!Initialize()) throw new System.InvalidOperationException("GPU描画条件を満たさないため、点群を設定できません。");
        if (data == null || data.Length == 0)
            throw new System.ArgumentException("点群データが空です。", nameof(data));

        int count = data.Length;

        Vector3[] nextCachedPositions = new Vector3[count];
        Color[] nextCachedColors = new Color[count];

        Vector3 min = count > 0 ? data[0].position : Vector3.zero;
        Vector3 max = count > 0 ? data[0].position : Vector3.zero;

        for (int i = 0; i < count; i++)
        {
            nextCachedPositions[i] = data[i].position;
            nextCachedColors[i] = PointData.UnpackColor(data[i].originalColor);

            min = Vector3.Min(min, data[i].position);
            max = Vector3.Max(max, data[i].position);
        }

        Bounds nextBounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 0.5f);
        Dictionary<string, byte[]> nextAnnotationLayers = CreateAnnotationLayers(data);
        ComputeBuffer nextPointBuffer = CreatePointBuffer(data);
        ComputeBuffer nextFullIndexBuffer;
        try { nextFullIndexBuffer = CreateFullIndexBuffer(count); }
        catch { ReleaseBufferSafely(nextPointBuffer); throw; }

        ComputeBuffer previousPointBuffer = pointBuffer;
        ComputeBuffer previousFullIndexBuffer = fullIndexBuffer;
        pointData = data;
        cachedPositions = nextCachedPositions;
        cachedColors = nextCachedColors;
        localBounds = nextBounds;
        pointBuffer = nextPointBuffer;
        fullIndexBuffer = nextFullIndexBuffer;
        annotationLayers = nextAnnotationLayers;
        activeAnnotationLayer = "Default";
        Interlocked.Increment(ref datasetGeneration);
        Interlocked.Increment(ref contentRevision);
        ReleaseBufferSafely(previousPointBuffer);
        ReleaseBufferSafely(previousFullIndexBuffer);

        try { StartOctreeBuild(nextCachedPositions, fallbackIndexBufferAlreadyStaged: true); }
        catch (System.Exception ex) { UseFullPointListFallback(ex); }
    }

    private static Dictionary<string, byte[]> CreateAnnotationLayers(PointData[] data)
    {
        byte[] defaultLabels = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) defaultLabels[i] = (byte)(data[i].label & 0xFF);
        return new Dictionary<string, byte[]> { ["Default"] = defaultLabels };
    }

    private static ComputeBuffer CreatePointBuffer(PointData[] data)
    {
        return AtomicResourceFactory.CreateInitialized(data,
            () => new ComputeBuffer(data.Length, Marshal.SizeOf(typeof(PointData))),
            (buffer, points) => buffer.SetData(points),
            buffer => buffer.Release());
    }

    private static ComputeBuffer CreateFullIndexBuffer(int count)
    {
        int[] indices = new int[count];
        for (int i = 0; i < count; i++) indices[i] = i;
        return AtomicResourceFactory.CreateInitialized(indices,
            () => new ComputeBuffer(count, sizeof(int)),
            (buffer, values) => buffer.SetData(values),
            buffer => buffer.Release());
    }

    private static void ReleaseBufferSafely(ComputeBuffer buffer)
    {
        if (buffer == null) return;
        try { buffer.Release(); }
        catch (System.Exception ex) { Debug.LogWarning($"[RecoverableOperationError] GPUバッファの解放に失敗しました。\n{ex}"); }
    }

    private void UseFullPointListFallback(System.Exception exception)
    {
        lock (octreeLock)
        {
            octree = null;
            isOctreeReady = false;
            isOctreeBuilding = false;
            pendingOctree = null;
            hasPendingOctree = false;
            pendingOctreeError = exception;
        }
        Debug.LogWarning($"[RecoverableOperationError] オクトリー構築を開始できません。全点表示で続行します。\n{exception}");
    }

    public bool ApplyPointCoordinateCorrection(float correctionFactor)
    {
        if (pointData == null || float.IsNaN(correctionFactor) || float.IsInfinity(correctionFactor) || correctionFactor <= 0f)
        {
            return false;
        }
        Vector3[] previousPositions = cachedPositions;
        bool createdPreviousPositions = previousPositions == null || previousPositions.Length != pointData.Length;
        if (createdPreviousPositions)
        {
            previousPositions = new Vector3[pointData.Length];
            for (int i = 0; i < pointData.Length; i++) previousPositions[i] = pointData[i].position;
        }

        Vector3[] correctedPositions = new Vector3[pointData.Length];
        Vector3 min = Vector3.zero;
        Vector3 max = Vector3.zero;
        for (int i = 0; i < pointData.Length; i++)
        {
            Vector3 position = previousPositions[i] * correctionFactor;
            if (float.IsNaN(position.x) || float.IsInfinity(position.x) ||
                float.IsNaN(position.y) || float.IsInfinity(position.y) ||
                float.IsNaN(position.z) || float.IsInfinity(position.z)) return false;
            correctedPositions[i] = position;
            if (i == 0) min = max = position;
            else
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
        }

        Bounds previousBounds = localBounds;
        for (int i = 0; i < pointData.Length; i++)
        {
            PointData point = pointData[i];
            point.position = correctedPositions[i];
            pointData[i] = point;
        }
        cachedPositions = correctedPositions;
        localBounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 0.5f);
        try
        {
            if (pointBuffer != null) pointBuffer.SetData(pointData);
            StartOctreeBuild(correctedPositions);
            Interlocked.Increment(ref datasetGeneration);
            Interlocked.Increment(ref contentRevision);
            return true;
        }
        catch (System.Exception applyException)
        {
            for (int i = 0; i < pointData.Length; i++)
            {
                PointData point = pointData[i];
                point.position = previousPositions[i];
                pointData[i] = point;
            }
            cachedPositions = previousPositions;
            localBounds = previousBounds;
            try
            {
                if (pointBuffer != null) pointBuffer.SetData(pointData);
            }
            catch (System.Exception rollbackException)
            {
                throw new System.AggregateException("座標補正とGPUバッファ復元に失敗しました。CPU側の元座標は復元済みです。",
                    applyException, rollbackException);
            }
            if (createdPreviousPositions) cachedPositions = previousPositions;
            throw;
        }
    }

    private int octreeBuildVersion = 0;
    private CancellationTokenSource octreeBuildCancellation;

    private void StartOctreeBuild(Vector3[] positions, ComputeBuffer preparedFullIndexBuffer = null,
        bool fallbackIndexBufferAlreadyStaged = false)
    {
        ComputeBuffer nextFullIndexBuffer = fallbackIndexBufferAlreadyStaged
            ? fullIndexBuffer
            : preparedFullIndexBuffer ?? CreateFullIndexBuffer(positions.Length);
        bool publishedFallbackBuffer = fallbackIndexBufferAlreadyStaged;
        int currentVersion;
        CancellationToken buildToken;
        try
        {
            lock (octreeLock)
            {
                octreeBuildCancellation?.Cancel();
                octreeBuildCancellation?.Dispose();
                octreeBuildCancellation = new CancellationTokenSource();
                buildToken = octreeBuildCancellation.Token;
                isOctreeReady = false;
                isOctreeBuilding = true;
                hasPendingOctree = false;
                pendingOctree = null;
                pendingOctreeError = null;
                octreeBuildVersion++;
                currentVersion = octreeBuildVersion;

                if (!fallbackIndexBufferAlreadyStaged)
                {
                    ComputeBuffer previousFallbackBuffer = fullIndexBuffer;
                    fullIndexBuffer = nextFullIndexBuffer;
                    publishedFallbackBuffer = true;
                    ReleaseBufferSafely(previousFallbackBuffer);
                }
            }

            int maxPoints = maxPointsPerNode;
            int maxDepth = maxOctreeDepth;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    buildToken.ThrowIfCancellationRequested();
                    var newOctree = new PointCloudOctree();
                    newOctree.Build(positions, maxPoints, maxDepth, buildToken);

                    lock (octreeLock)
                    {
                        if (currentVersion == octreeBuildVersion && !buildToken.IsCancellationRequested)
                        {
                            pendingOctree = newOctree;
                            hasPendingOctree = true;
                        }
                    }
                }
                catch (System.OperationCanceledException)
                {
                    lock (octreeLock)
                    {
                        if (currentVersion == octreeBuildVersion) isOctreeBuilding = false;
                    }
                }
                catch (System.Exception ex)
                {
                    lock (octreeLock)
                    {
                        if (currentVersion == octreeBuildVersion)
                        {
                            pendingOctreeError = ex;
                            isOctreeBuilding = false;
                        }
                    }
                }
            });
        }
        catch
        {
            if (!publishedFallbackBuffer) ReleaseBufferSafely(nextFullIndexBuffer);
            else
            {
                lock (octreeLock) isOctreeBuilding = false;
            }
            throw;
        }
    }

    void Update()
    {
        System.Exception octreeError = null;
        lock (octreeLock)
        {
            if (pendingOctreeError != null)
            {
                octreeError = pendingOctreeError;
                pendingOctreeError = null;
            }
        }
        if (octreeError != null)
            Debug.LogWarning($"[RecoverableOperationError] Octree構築に失敗しました。線形検索で続行します。\n{octreeError}");

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
        if (!TryUpdatePointBuffer())
        {
            Debug.LogWarning($"[PointCloudRenderer] UpdatePointBuffer skipped: pointBuffer is null = {pointBuffer == null}, pointData is null = {pointData == null}");
        }
    }

    public bool TryUpdatePointBuffer()
    {
        if (pointBuffer == null || pointData == null) return false;
        pointBuffer.SetData(pointData);
        Interlocked.Increment(ref contentRevision);
        return true;
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
        // Public limits are millimeters; the shader receives data-space values below.
        colorMode = 1;
        minHeight = minH;
        maxHeight = maxH;
    }

    public void ShowDistanceMap(float[] distances, float maxDistThreshold)
    {
        // Contract: producer distances and threshold are both millimeters.
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
            if (HardwareCompatibilityDiagnostic.HasBlockingGraphicsFailure) return;
            isInitialized = false;
            if (!Initialize()) return;
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
        Transform display = DisplayTransform;
        Vector3 displayScale = display.lossyScale;

        if (useLOD)
        {
            visibleIndices.Clear();
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            Vector3 camPos = cam.transform.position;
            float scale = Mathf.Max(Mathf.Abs(displayScale.x), Mathf.Max(Mathf.Abs(displayScale.y), Mathf.Abs(displayScale.z)));

            TraverseOctree(octree.root, display, planes, camPos, scale, lodThreshold, visibleIndices);
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
                fullIndexBuffer = CreateFullIndexBuffer(pointData.Length);
            }
            pointMaterial.SetBuffer("_Indices", fullIndexBuffer);
            drawCount = pointData.Length;
        }

        pointMaterial.SetFloat("_PointSize", pointSize);
        pointMaterial.SetInt("_ColorMode", colorMode);
        pointMaterial.SetFloat("_MinHeight", MillimetersToDataLength(minHeight));
        pointMaterial.SetFloat("_MaxHeight", MillimetersToDataLength(maxHeight));
        pointMaterial.SetFloat("_MaxDistanceThreshold", maxDistanceThreshold);
        pointMaterial.SetMatrix("_LocalToWorld", display.localToWorldMatrix);

        // Transform local bounds to world space for camera culling
        Bounds worldBounds = new Bounds(
            display.TransformPoint(localBounds.center),
            Vector3.Scale(localBounds.size, new Vector3(Mathf.Abs(displayScale.x), Mathf.Abs(displayScale.y), Mathf.Abs(displayScale.z)))
        );

        activeDrawCount = drawCount;
        if (drawCount > 0)
        {
            // Graphics.DrawProcedural renders directly. Triangles, 6 vertices per point (1 quad)
            Graphics.DrawProcedural(pointMaterial, worldBounds, MeshTopology.Triangles, drawCount * 6);
        }
    }

    private void TraverseOctree(PointCloudOctree.Node node, Transform display, Plane[] planes, Vector3 camPos, float scale, float currentThreshold, List<int> outIndices)
    {
        if (node == null) return;

        // 1. Transform bounds sphere to world space
        Vector3 worldCenter = display.TransformPoint(node.center);
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
                TraverseOctree(node.children[i], display, planes, camPos, scale, currentThreshold, outIndices);
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
        CancelOctreeBuild();
        ReleaseBuffers();
    }

    void OnDestroy()
    {
        CancelOctreeBuild();
        ReleaseBuffers();

        if (pointMaterial != null)
        {
            Destroy(pointMaterial);
        }
    }

    private void CancelOctreeBuild()
    {
        lock (octreeLock)
        {
            octreeBuildVersion++;
            if (octreeBuildCancellation != null)
            {
                try { octreeBuildCancellation.Cancel(); }
                catch (System.ObjectDisposedException) { }
                octreeBuildCancellation.Dispose();
                octreeBuildCancellation = null;
            }
            hasPendingOctree = false;
            pendingOctree = null;
            isOctreeBuilding = false;
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
