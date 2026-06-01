using UnityEngine;
using System.Collections.Generic;

public class PointCloudManager : MonoBehaviour
{
    [Header("Point Cloud Targets")]
    public PointCloudRenderer referenceCloud;
    public PointCloudRenderer alignedCloud;

    [Header("Camera Setup")]
    public Transform cameraRig; // e.g., XR Origin or Main Camera parent

    [Header("PC Control Settings")]
    public float cameraMoveSpeed = 4.0f;
    public float cameraRotateSpeed = 100.0f;

    // Control Modes
    public enum ControlMode { Camera, AlignedObject }
    private ControlMode currentMode = ControlMode.Camera;

    // Visualization Options
    public enum ColorMode { Original, HeightMap, DistanceHeatmap }
    private ColorMode currentColorMode = ColorMode.Original;

    // Parameters
    private float pointSize = 2.0f;
    private float maxDistanceThreshold = 1.0f;
    private float[] calculatedDistances;
    private bool hasCompared = false;

    // Stats
    private float avgDistance = 0f;
    private float maxDistance = 0f;
    private int comparedPointCount = 0;

    // UI Styles
    private GUIStyle windowStyle;
    private GUIStyle headerStyle;
    private GUIStyle buttonStyle;
    private GUIStyle activeButtonStyle;
    private GUIStyle textStyle;
    private bool stylesInitialized = false;

    private CloudCompareCameraController ccCameraController;

    // Loader references
    private PointCloudLoader refLoader;
    private PointCloudLoader alignLoader;

    void Start()
    {
        // Find or add CloudCompareCameraController
        Camera cam = Camera.main;
        if (cam != null)
        {
            ccCameraController = cam.GetComponent<CloudCompareCameraController>();
            if (ccCameraController == null)
            {
                ccCameraController = cam.gameObject.AddComponent<CloudCompareCameraController>();
            }
        }

        // Try to automatically find components if not assigned
        if (referenceCloud == null)
        {
            GameObject refGo = GameObject.Find("ReferenceCloud");
            if (refGo != null) referenceCloud = refGo.GetComponent<PointCloudRenderer>();
        }
        if (alignedCloud == null)
        {
            GameObject alignGo = GameObject.Find("AlignedCloud");
            if (alignGo != null) alignedCloud = alignGo.GetComponent<PointCloudRenderer>();
        }

        if (cameraRig == null)
        {
            // Find XR Origin or Main Camera
            var xrOrigin = Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (xrOrigin != null)
            {
                cameraRig = xrOrigin.transform;
            }
            else if (cam != null)
            {
                cameraRig = cam.transform.parent != null ? cam.transform.parent : cam.transform;
            }
        }

        // Setup controller active flags
        UpdateControlStates();

        // Sync initial point size to renderers
        if (referenceCloud != null) referenceCloud.SetPointSize(pointSize);
        if (alignedCloud != null) alignedCloud.SetPointSize(pointSize);
    }

    void Update()
    {
        // Toggle Control Mode with Tab key
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            currentMode = (currentMode == ControlMode.Camera) ? ControlMode.AlignedObject : ControlMode.Camera;
            UpdateControlStates();
            Debug.Log($"[PointCloudManager] Control mode changed to: {currentMode}");
        }

        // Handle camera movement if in Camera mode
        if (currentMode == ControlMode.Camera && cameraRig != null)
        {
            HandleCameraMovement();
        }
    }

    private void UpdateControlStates()
    {
        if (ccCameraController != null)
        {
            ccCameraController.enabled = (currentMode == ControlMode.Camera);
        }

        if (alignedCloud != null)
        {
            var controller = alignedCloud.GetComponent<PointCloudController>();
            if (controller != null)
            {
                // Enable PointCloudController PC controls only when we want to align the object
                controller.isControlEnabled = (currentMode == ControlMode.AlignedObject);
            }
        }
        
        if (referenceCloud != null)
        {
            var controller = referenceCloud.GetComponent<PointCloudController>();
            if (controller != null)
            {
                // Reference cloud is always static
                controller.isControlEnabled = false;
            }
        }
    }

    private void HandleCameraMovement()
    {
        if (ccCameraController != null) return; // Let CloudCompareCameraController handle it on PC

        // Get WASD/QE movement
        float h = 0f;
        float v = 0f;
        float vertical = 0f;

        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v = 1.0f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v = -1.0f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h = 1.0f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h = -1.0f;
        if (Input.GetKey(KeyCode.E)) vertical = 1.0f;
        if (Input.GetKey(KeyCode.Q)) vertical = -1.0f;

        if (Mathf.Abs(h) > 0.01f || Mathf.Abs(v) > 0.01f)
        {
            Vector3 moveDir = (cameraRig.forward * v + cameraRig.right * h);
            moveDir.y = 0; // Keep movement horizontal
            moveDir.Normalize();
            cameraRig.Translate(moveDir * cameraMoveSpeed * Time.deltaTime, Space.World);
        }

        if (Mathf.Abs(vertical) > 0.01f)
        {
            cameraRig.Translate(Vector3.up * vertical * cameraMoveSpeed * Time.deltaTime, Space.World);
        }

        // Camera Look rotation with Right Click drag
        if (Input.GetMouseButton(1))
        {
            float rotX = Input.GetAxis("Mouse X") * cameraRotateSpeed * Time.deltaTime;
            float rotY = -Input.GetAxis("Mouse Y") * cameraRotateSpeed * Time.deltaTime;

            cameraRig.Rotate(Vector3.up, rotX, Space.World);
            cameraRig.Rotate(Vector3.right, rotY, Space.Self);
            
            // Constrain roll
            Vector3 euler = cameraRig.localEulerAngles;
            euler.z = 0;
            cameraRig.localEulerAngles = euler;
        }
    }

    // Auto-center alignment (ICP level 1)
    public void AlignCenters()
    {
        if (referenceCloud == null || alignedCloud == null) return;

        Vector3[] refPos = referenceCloud.GetPositions();
        Vector3[] alignPos = alignedCloud.GetPositions();

        if (refPos == null || alignPos == null || refPos.Length == 0 || alignPos.Length == 0)
        {
            Debug.LogWarning("[PointCloudManager] Cannot align: point cloud positions are empty.");
            return;
        }

        // Calculate world space center of Reference Cloud
        Vector3 refCenter = Vector3.zero;
        foreach (var p in refPos)
        {
            refCenter += referenceCloud.transform.TransformPoint(p);
        }
        refCenter /= refPos.Length;

        // Calculate world space center of Aligned Cloud
        Vector3 alignCenter = Vector3.zero;
        foreach (var p in alignPos)
        {
            alignCenter += alignedCloud.transform.TransformPoint(p);
        }
        alignCenter /= alignPos.Length;

        // Offset aligned object by the difference
        Vector3 offset = refCenter - alignCenter;
        alignedCloud.transform.position += offset;

        Debug.Log($"[PointCloudManager] Center alignment complete. Offset applied: {offset}");
    }

    // Fast Grid-Based Cloud-to-Cloud Distance Calculation
    public void CompareClouds()
    {
        if (referenceCloud == null || alignedCloud == null) return;

        Vector3[] refPos = referenceCloud.GetPositions();
        Vector3[] alignPos = alignedCloud.GetPositions();

        if (refPos == null || alignPos == null || refPos.Length == 0 || alignPos.Length == 0)
        {
            Debug.LogError("[PointCloudManager] Missing points data for C2C comparison.");
            return;
        }

        int nRef = refPos.Length;
        int nAlign = alignPos.Length;
        calculatedDistances = new float[nAlign];

        // 1. Transform Reference points to World Space
        Vector3[] refWorld = new Vector3[nRef];
        for (int i = 0; i < nRef; i++)
        {
            refWorld[i] = referenceCloud.transform.TransformPoint(refPos[i]);
        }

        // 2. Transform Aligned points to World Space
        Vector3[] alignWorld = new Vector3[nAlign];
        for (int i = 0; i < nAlign; i++)
        {
            alignWorld[i] = alignedCloud.transform.TransformPoint(alignPos[i]);
        }

        // 3. Determine bounding box of Reference Cloud to auto-size grid cells
        Vector3 min = refWorld[0];
        Vector3 max = refWorld[0];
        for (int i = 1; i < nRef; i++)
        {
            min = Vector3.Min(min, refWorld[i]);
            max = Vector3.Max(max, refWorld[i]);
        }
        float sizeX = max.x - min.x;
        float sizeY = max.y - min.y;
        float sizeZ = max.z - min.z;
        float maxAxis = Mathf.Max(sizeX, Mathf.Max(sizeY, sizeZ));
        
        // Grid cell size is 2% of the largest dimension (typical search range)
        float cellSize = maxAxis * 0.02f;
        if (cellSize < 0.01f) cellSize = 0.1f;

        // 4. Populate Grid
        Dictionary<Vector3Int, List<Vector3>> grid = new Dictionary<Vector3Int, List<Vector3>>();
        for (int i = 0; i < nRef; i++)
        {
            Vector3 p = refWorld[i];
            Vector3Int key = new Vector3Int(
                Mathf.FloorToInt(p.x / cellSize),
                Mathf.FloorToInt(p.y / cellSize),
                Mathf.FloorToInt(p.z / cellSize)
            );

            if (!grid.ContainsKey(key))
            {
                grid[key] = new List<Vector3>();
            }
            grid[key].Add(p);
        }

        // 5. Query Closest Point for each point in Aligned Cloud
        float sumDist = 0f;
        maxDistance = 0f;

        for (int i = 0; i < nAlign; i++)
        {
            Vector3 p = alignWorld[i];
            Vector3Int cellKey = new Vector3Int(
                Mathf.FloorToInt(p.x / cellSize),
                Mathf.FloorToInt(p.y / cellSize),
                Mathf.FloorToInt(p.z / cellSize)
            );

            float minDistSq = float.MaxValue;
            bool foundInGrid = false;

            // Search self + 26 surrounding grid cells
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        Vector3Int neighborKey = cellKey + new Vector3Int(dx, dy, dz);
                        if (grid.ContainsKey(neighborKey))
                        {
                            foreach (Vector3 refPt in grid[neighborKey])
                            {
                                float distSq = (p - refPt).sqrMagnitude;
                                if (distSq < minDistSq)
                                {
                                    minDistSq = distSq;
                                    foundInGrid = true;
                                }
                            }
                        }
                    }
                }
            }

            // Fallback: if no points in nearby grid, look up via brute force (or assign threshold)
            if (!foundInGrid)
            {
                // Subsampled fallback to avoid complete freeze
                int step = Mathf.Max(1, nRef / 1000); // sample 1000 points
                for (int j = 0; j < nRef; j += step)
                {
                    float distSq = (p - refWorld[j]).sqrMagnitude;
                    if (distSq < minDistSq)
                    {
                        minDistSq = distSq;
                    }
                }
            }

            float distance = Mathf.Sqrt(minDistSq);
            calculatedDistances[i] = distance;

            sumDist += distance;
            if (distance > maxDistance) maxDistance = distance;
        }

        comparedPointCount = nAlign;
        avgDistance = sumDist / nAlign;
        hasCompared = true;

        Debug.Log($"[PointCloudManager] C2C calculation complete. Avg Distance: {avgDistance:F4}m, Max Distance: {maxDistance:F4}m");

        // Force colors update
        UpdateColors();
    }

    public void UpdateColors()
    {
        if (referenceCloud == null || alignedCloud == null) return;

        if (currentColorMode == ColorMode.Original)
        {
            referenceCloud.ShowOriginalColors();
            alignedCloud.ShowOriginalColors();
        }
        else if (currentColorMode == ColorMode.HeightMap)
        {
            // Determine combined height limits
            float minH = -2.0f;
            float maxH = 5.0f;
            referenceCloud.ShowHeightMap(minH, maxH);
            alignedCloud.ShowHeightMap(minH, maxH);
        }
        else if (currentColorMode == ColorMode.DistanceHeatmap)
        {
            if (hasCompared && calculatedDistances != null)
            {
                referenceCloud.ShowOriginalColors(); // Keep reference original
                alignedCloud.ShowDistanceMap(calculatedDistances, maxDistanceThreshold); // Update aligned cloud to heatmap
            }
            else
            {
                Debug.LogWarning("[PointCloudManager] Run C2C comparison first before enabling Distance Map.");
                currentColorMode = ColorMode.Original;
                UpdateColors();
            }
        }
    }

    public void ResetAlignedPosition()
    {
        if (alignedCloud != null)
        {
            var controller = alignedCloud.GetComponent<PointCloudController>();
            if (controller != null)
            {
                controller.ResetTransform();
            }
            else
            {
                alignedCloud.transform.localPosition = Vector3.zero;
                alignedCloud.transform.localRotation = Quaternion.identity;
                alignedCloud.transform.localScale = Vector3.one;
            }
        }
    }

    private void InitializeStyles()
    {
        if (stylesInitialized) return;

        // Custom premium dark theme styling for OnGUI
        Texture2D bgTexture = new Texture2D(1, 1);
        bgTexture.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.16f, 0.85f)); // Glassmorphism dark indigo transparent
        bgTexture.Apply();

        windowStyle = new GUIStyle(GUI.skin.box);
        windowStyle.normal.background = bgTexture;
        windowStyle.padding = new RectOffset(15, 15, 15, 15);

        headerStyle = new GUIStyle();
        headerStyle.fontSize = 20;
        headerStyle.fontStyle = FontStyle.Bold;
        headerStyle.normal.textColor = Color.white;
        headerStyle.alignment = TextAnchor.MiddleCenter;
        headerStyle.margin = new RectOffset(0, 0, 0, 10);

        buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.fontSize = 14;
        buttonStyle.fontStyle = FontStyle.Bold;
        buttonStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
        buttonStyle.hover.textColor = Color.white;
        buttonStyle.padding = new RectOffset(10, 10, 8, 8);
        buttonStyle.margin = new RectOffset(0, 0, 4, 4);

        activeButtonStyle = new GUIStyle(buttonStyle);
        Texture2D activeBg = new Texture2D(1, 1);
        activeBg.SetPixel(0, 0, new Color(0.2f, 0.45f, 0.85f, 1f)); // Vibrant Blue
        activeBg.Apply();
        activeButtonStyle.normal.background = activeBg;
        activeButtonStyle.normal.textColor = Color.white;

        textStyle = new GUIStyle(GUI.skin.label);
        textStyle.fontSize = 13;
        textStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
        textStyle.margin = new RectOffset(0, 0, 2, 2);

        stylesInitialized = true;
    }

    void OnGUI()
    {
        InitializeStyles();

        // 400x540 size control window on the right side of the screen
        float width = 400f;
        float height = 540f;
        float posX = Screen.width - width - 20f;
        float posY = 220f; // Shift down to avoid top bar
 
        GUILayout.BeginArea(new Rect(posX, posY, width, height), windowStyle);
 
        GUILayout.Label("☁ CloudCompare Unity機能パネル", headerStyle);
        GUILayout.Box("", GUILayout.Height(2)); // Separator line
        GUILayout.Space(10);

        // --- 1. Target Controls Selection ---
        GUILayout.Label("🎮 操作モード", textStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("カメラ視点操作 (WASD)", currentMode == ControlMode.Camera ? activeButtonStyle : buttonStyle))
        {
            currentMode = ControlMode.Camera;
            UpdateControlStates();
        }
        if (GUILayout.Button("点群位置合わせ (オブジェクト操作)", currentMode == ControlMode.AlignedObject ? activeButtonStyle : buttonStyle))
        {
            currentMode = ControlMode.AlignedObject;
            UpdateControlStates();
        }
        GUILayout.EndHorizontal();
        GUILayout.Label("ヒント: [Tab] キーでカメラ操作と点群操作を切り替えられます。", textStyle);
        GUILayout.Space(15);

        // --- 2. Color Map / Scalar Fields Mode ---
        GUILayout.Label("🎨 カラー表示モード", textStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("オリジナルRGB", currentColorMode == ColorMode.Original ? activeButtonStyle : buttonStyle))
        {
            currentColorMode = ColorMode.Original;
            UpdateColors();
        }
        if (GUILayout.Button("高さマップ (Height Map)", currentColorMode == ColorMode.HeightMap ? activeButtonStyle : buttonStyle))
        {
            currentColorMode = ColorMode.HeightMap;
            UpdateColors();
        }
        if (GUILayout.Button("距離ヒートマップ (C2C)", currentColorMode == ColorMode.DistanceHeatmap ? activeButtonStyle : buttonStyle))
        {
            if (hasCompared)
            {
                currentColorMode = ColorMode.DistanceHeatmap;
                UpdateColors();
            }
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(15);

        // --- 3. Rendering Adjustments ---
        GUILayout.Label($"⚪ 点のサイズ (Pixels): {pointSize:F0}", textStyle);
        float newSize = GUILayout.HorizontalSlider(pointSize, 1.0f, 20.0f);
        if (Mathf.Abs(newSize - pointSize) > 0.1f)
        {
            pointSize = Mathf.Round(newSize);
            if (referenceCloud != null) referenceCloud.SetPointSize(pointSize);
            if (alignedCloud != null) alignedCloud.SetPointSize(pointSize);
        }
        GUILayout.Space(10);

        // --- 4. Alignment Tools ---
        GUILayout.Label("⚙ 位置合わせ ＆ ICP ツール", textStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("中心位置を合わせる", buttonStyle))
        {
            AlignCenters();
        }
        if (GUILayout.Button("位置リセット", buttonStyle))
        {
            ResetAlignedPosition();
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(15);

        // --- 5. Analysis / Cloud-to-Cloud Distance Comparison ---
        GUILayout.Label("🔬 変化検出 ＆ C2C 距離計算", textStyle);
        if (GUILayout.Button("C2C 距離計算を実行 (Grid検索)", activeButtonStyle))
        {
            CompareClouds();
        }
        GUILayout.Space(10);

        GUILayout.Label($"C2C カラーしきい値: {maxDistanceThreshold:F2}m", textStyle);
        float newThreshold = GUILayout.HorizontalSlider(maxDistanceThreshold, 0.05f, 5.0f);
        if (Mathf.Abs(newThreshold - maxDistanceThreshold) > 0.01f)
        {
            maxDistanceThreshold = newThreshold;
            if (currentColorMode == ColorMode.DistanceHeatmap && hasCompared)
            {
                UpdateColors();
            }
        }
        GUILayout.Space(15);

        // --- 6. Analytics Stats Window ---
        GUILayout.Box("", GUILayout.Height(2)); // Separator line
        GUILayout.Label("📊 C2C 比較統計結果", textStyle);
        if (hasCompared)
        {
            GUILayout.Label($"比較対象点数: {comparedPointCount:N0}", textStyle);
            GUILayout.Label($"平均距離偏差: {avgDistance:F5} m", textStyle);
            GUILayout.Label($"最大距離偏差: {maxDistance:F5} m", textStyle);
        }
        else
        {
            GUILayout.Label("C2C距離計算が未実行です。上のボタンを押してください。", textStyle);
        }

        GUILayout.EndArea();
    }
}
