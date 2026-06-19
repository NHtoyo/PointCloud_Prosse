using UnityEngine;
using System.Collections.Generic;
using PointCloudWorkbench;

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
    public enum ColorMode { Original, Annotation }
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
    private float currentScaleFactor = 1.0f;

    // UI Styles
    private GUIStyle windowStyle;
    private GUIStyle headerStyle;
    private GUIStyle buttonStyle;
    private GUIStyle activeButtonStyle;
    private GUIStyle textStyle;
    private GUIStyle foldoutHeaderStyle;
    private GUIStyle toggleStyle;
    private bool stylesInitialized = false;

    // LOD & Stats variables ported from PointCloudEditorUI
    private PointCloudEditor editorInstance;
    private PointCloudEditorUI editorUIInstance;
    private AnnotationPipelineEditorUI annotationUI;
    private Vector2 rightScrollPos;
    private bool foldoutLOD = true;
    private bool foldoutStats = true;

    // Legend UI styles and textures
    private Texture2D legendBgTexture;
    private Texture2D colorTexture;
    private GUIStyle legendStyle;
    private GUIStyle legendTitleStyle;
    private GUIStyle legendTextStyle;
    private bool legendStylesInitialized = false;

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
            var xrOrigin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
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
        // テキスト入力フィールドにフォーカスがある場合はキー入力を無視する（IMEやBackspaceの競合を回避）
        if (GUIUtility.keyboardControl == 0 && Input.GetKeyDown(KeyCode.Tab))
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
        if (currentColorMode == ColorMode.Original)
        {
            if (referenceCloud != null) referenceCloud.ShowOriginalColors();
            if (alignedCloud != null) alignedCloud.ShowOriginalColors();
        }
        else if (currentColorMode == ColorMode.Annotation)
        {
            if (referenceCloud != null) referenceCloud.colorMode = 2; // Label/Annotation mode in shader
            if (alignedCloud != null) alignedCloud.colorMode = 2;     // Label/Annotation mode in shader
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
                alignedCloud.transform.localScale = new Vector3(currentScaleFactor, currentScaleFactor, currentScaleFactor);
            }
        }
    }

    /// <summary>
    /// スケール校正レポートファイルをロードし、点群オブジェクトのTransformスケールに自動適用します。
    /// </summary>
    public void ApplyScaleCalibration()
    {
        string jsonPath = PointCloudWorkbench.PointCloudScaleService.GetDefaultReportPath();
        float scaleMetersPerUnit = PointCloudWorkbench.PointCloudScaleService.LoadMetersPerUnitOrDefault(jsonPath);
        Debug.Log($"[ScaleManager] Applying scale calibration: {scaleMetersPerUnit:F6} m/unit");

        currentScaleFactor = scaleMetersPerUnit;
        PointCloudWorkbench.PointCloudScaleService.ApplyUniformScale(referenceCloud, scaleMetersPerUnit);
        PointCloudWorkbench.PointCloudScaleService.ApplyUniformScale(alignedCloud, scaleMetersPerUnit);
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

        Texture2D foldoutBg = new Texture2D(1, 1);
        foldoutBg.SetPixel(0, 0, new Color(0.18f, 0.22f, 0.28f, 0.9f));
        foldoutBg.Apply();

        foldoutHeaderStyle = new GUIStyle(GUI.skin.button);
        foldoutHeaderStyle.fontSize = 14;
        foldoutHeaderStyle.fontStyle = FontStyle.Bold;
        foldoutHeaderStyle.alignment = TextAnchor.MiddleLeft;
        foldoutHeaderStyle.normal.textColor = Color.white;
        foldoutHeaderStyle.padding = new RectOffset(10, 10, 6, 6);
        foldoutHeaderStyle.normal.background = foldoutBg;

        toggleStyle = new GUIStyle(GUI.skin.toggle);
        toggleStyle.fontSize = 14;
        toggleStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
        toggleStyle.hover.textColor = Color.white;
        toggleStyle.margin = new RectOffset(0, 0, 3, 3);

        stylesInitialized = true;
    }

    void OnGUI()
    {
        InitializeStyles();

        // 460x930 size control window on the right side of the screen (height increased to match left UI and fit new sections)
        float width = 460f;
        float height = 930f;
        float posX = Screen.width - width - 20f;
        float posY = 20f;
 
        GUILayout.BeginArea(new Rect(posX, posY, width, height), windowStyle);
 
        GUILayout.Label("☁ CloudCompare Unity機能パネル", headerStyle);
        GUILayout.Box("", GUILayout.Height(2)); // Separator line
        GUILayout.Space(5);

        // Scrollview to fit everything cleanly
        rightScrollPos = GUILayout.BeginScrollView(rightScrollPos, GUILayout.Width(width - 15), GUILayout.Height(height - 40));

        // Find PointCloudEditor if not found
        if (editorInstance == null)
        {
            editorInstance = Object.FindAnyObjectByType<PointCloudEditor>();
        }
        if (annotationUI == null)
        {
            annotationUI = Object.FindAnyObjectByType<AnnotationPipelineEditorUI>();
        }
        if (editorUIInstance == null)
        {
            editorUIInstance = Object.FindAnyObjectByType<PointCloudEditorUI>();
        }

        // --- 1. Target Controls Selection ---
        GUILayout.Label("🎮 操作モード", textStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("カメラ視点操作", currentMode == ControlMode.Camera ? activeButtonStyle : buttonStyle))
        {
            currentMode = ControlMode.Camera;
            UpdateControlStates();
        }
        if (GUILayout.Button("点群位置合わせ", currentMode == ControlMode.AlignedObject ? activeButtonStyle : buttonStyle))
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
        if (GUILayout.Button("アノテーション表示", currentColorMode == ColorMode.Annotation ? activeButtonStyle : buttonStyle))
        {
            currentColorMode = ColorMode.Annotation;
            UpdateColors();
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(15);

        // --- 3. Rendering Adjustments ---
        GUILayout.Label($"⚪ 点のサイズ: {pointSize:F0}", textStyle);
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
        if (GUILayout.Button("C2C 距離計算を実行", activeButtonStyle))
        {
            CompareClouds();
        }
        GUILayout.Space(10);

        GUILayout.Label($"C2C カラーしきい値: {maxDistanceThreshold:F2}m", textStyle);
        float newThreshold = GUILayout.HorizontalSlider(maxDistanceThreshold, 0.05f, 5.0f);
        if (Mathf.Abs(newThreshold - maxDistanceThreshold) > 0.01f)
        {
            maxDistanceThreshold = newThreshold;
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
        GUILayout.Space(15);

        if (editorUIInstance != null)
        {
            GUILayout.Box("", GUILayout.Height(1));
            GUILayout.Space(5);

            GUILayout.Label("🔌 ツールパネル表示トグル", textStyle);
            GUILayout.BeginHorizontal();
            bool prevAnn = editorUIInstance.showAnnotationUI;
            bool prevNoise = editorUIInstance.showNoiseFilterUI;
            bool prevMeas = editorUIInstance.showMeasurementUI;

            editorUIInstance.showAnnotationUI = GUILayout.Toggle(editorUIInstance.showAnnotationUI, " アノテーションUI", toggleStyle);
            editorUIInstance.showNoiseFilterUI = GUILayout.Toggle(editorUIInstance.showNoiseFilterUI, " モヤ処理UI", toggleStyle);
            editorUIInstance.showMeasurementUI = GUILayout.Toggle(editorUIInstance.showMeasurementUI, " 二点間距離計測UI", toggleStyle);

            if (editorUIInstance.showAnnotationUI != prevAnn || 
                editorUIInstance.showNoiseFilterUI != prevNoise || 
                editorUIInstance.showMeasurementUI != prevMeas)
            {
                editorUIInstance.SaveSettings();
            }
            GUILayout.EndHorizontal();
            
            GUILayout.Space(15);
            GUILayout.Box("", GUILayout.Height(1));
            GUILayout.Space(10);

            // Extension Buttons
            GUILayout.Label("⚖ スケール同定 & ダウンサンプリング", textStyle);
            GUILayout.Space(10);

            if (GUILayout.Button("📐 スケール校正を実行 (基準球実寸設定)", activeButtonStyle, GUILayout.Height(45)))
            {
                editorUIInstance.showScaleCalibDialog = true;
                editorUIInstance.showDownsampleDialog = false;
            }
            GUILayout.Space(10);



            if (GUILayout.Button("📥 ダウンサンプリング処理実行", activeButtonStyle, GUILayout.Height(45)))
            {
                editorUIInstance.showDownsampleDialog = true;
                editorUIInstance.showScaleCalibDialog = false;
            }
            GUILayout.Space(15);
        }

        // --- 7. Ported: LOD & Culling settings (Foldout) ---
        if (editorInstance != null && editorInstance.targetRenderer != null)
        {
            foldoutLOD = GUILayout.Toggle(foldoutLOD, (foldoutLOD ? "▼ " : "▶ ") + "💻 レンダリング最適化", foldoutHeaderStyle);
            if (foldoutLOD)
            {
                GUILayout.Space(3);
                var rend = editorInstance.targetRenderer;
                rend.enableLOD = GUILayout.Toggle(rend.enableLOD, " LOD・カリングを有効化");
                
                if (rend.enableLOD)
                {
                    GUILayout.Label($"  LOD閾値: {rend.lodThreshold:F4}", textStyle);
                    rend.lodThreshold = GUILayout.HorizontalSlider(rend.lodThreshold, 0.005f, 0.1f);
                }
                
                if (rend.IsOctreeBuilding)
                {
                    GUILayout.Label("  ⏳ オクトリーを構築中...", textStyle);
                }
                else if (rend.IsOctreeReady)
                {
                    GUILayout.Label("  ✅ オクトリー構築完了 (LOD有効)", textStyle);
                }
                GUILayout.Space(8);
            }
        }

        // --- 8. Ported: Dataset Statistics (Foldout) ---
        if (editorInstance != null && editorInstance.targetRenderer != null)
        {
            int totalPoints = editorInstance.targetRenderer.GetPointData() != null ? editorInstance.targetRenderer.GetPointData().Length : 0;
            foldoutStats = GUILayout.Toggle(foldoutStats, (foldoutStats ? "▼ " : "▶ ") + "📊 データセット統計", foldoutHeaderStyle);
            if (foldoutStats)
            {
                GUILayout.Space(3);
                GUILayout.Label($"総点数: {totalPoints:N0}", textStyle);
                var rend = editorInstance.targetRenderer;
                if (rend.enableLOD)
                {
                    GUILayout.Label($"描画点数: {rend.GetActiveDrawCount():N0} (LOD率: {((float)rend.GetActiveDrawCount() / Mathf.Max(totalPoints, 1) * 100f):F1}%)", textStyle);
                }
                else
                {
                    GUILayout.Label($"描画点数: {totalPoints:N0} (LOD無効)", textStyle);
                }

                // Dynamic annotation counts
                Dictionary<int, int> countsMap = editorInstance.GetLabelCountsMap();
                if (annotationUI != null && annotationUI.GetActivePreset() != null)
                {
                    foreach (var cls in annotationUI.GetActivePreset().classes)
                    {
                        int count = countsMap.ContainsKey(cls.id) ? countsMap[cls.id] : 0;
                        GUILayout.Label($"  - {cls.name} ({cls.id}): {count:N0}", textStyle);
                    }
                }
                else
                {
                    // Fallback
                    int[] counts = editorInstance.GetLabelCounts();
                    GUILayout.Label($"  - 未分類 (0): {counts[0]:N0}", textStyle);
                    GUILayout.Label($"  - 茎 (1): {counts[1]:N0}", textStyle);
                    GUILayout.Label($"  - 葉 (2): {counts[2]:N0}", textStyle);
                    GUILayout.Label($"  - 果実 (3): {counts[3]:N0}", textStyle);
                    GUILayout.Label($"  - 花 (4): {counts[4]:N0}", textStyle);
                    GUILayout.Label($"  - 支柱 (5): {counts[5]:N0}", textStyle);
                }
                GUILayout.Label($"  - 削除済/ノイズ (物理非表示): {editorInstance.GetNoiseDeletedCount():N0}", textStyle);
                GUILayout.Space(5);
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();

        // Draw Annotation Legend in bottom left if currentColorMode == ColorMode.Annotation
        if (currentColorMode == ColorMode.Annotation)
        {
            DrawAnnotationLegend();
        }
    }

    private void InitializeLegendStyles()
    {
        if (legendStylesInitialized) return;

        legendBgTexture = new Texture2D(1, 1);
        legendBgTexture.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.16f, 0.85f)); // Glassmorphism dark indigo transparent
        legendBgTexture.Apply();

        colorTexture = new Texture2D(1, 1);
        colorTexture.SetPixel(0, 0, Color.white);
        colorTexture.Apply();

        legendStyle = new GUIStyle(GUI.skin.box);
        legendStyle.normal.background = legendBgTexture;
        legendStyle.padding = new RectOffset(15, 15, 15, 15);

        legendTitleStyle = new GUIStyle(GUI.skin.label);
        legendTitleStyle.fontSize = 16;
        legendTitleStyle.fontStyle = FontStyle.Bold;
        legendTitleStyle.normal.textColor = Color.white;
        legendTitleStyle.alignment = TextAnchor.MiddleLeft;

        legendTextStyle = new GUIStyle(GUI.skin.label);
        legendTextStyle.fontSize = 14;
        legendTextStyle.fontStyle = FontStyle.Bold;
        legendTextStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
        legendTextStyle.alignment = TextAnchor.MiddleLeft;

        legendStylesInitialized = true;
    }

    private void DrawAnnotationLegend()
    {
        InitializeLegendStyles();

        if (annotationUI == null || annotationUI.GetActivePreset() == null) return;
        var classes = annotationUI.GetActivePreset().classes;

        // Dynamic height calculation: ~25f per item + ~40f title margin
        float width = 360f;
        float height = 40f + classes.Count * 25f;
        float posX = 20f;
        float posY = Screen.height - height - 20f;

        // If Noise Filter Preview Legend is ALSO showing, offset Annotation Legend to the right so they don't overlap
        if (PointCloudWorkbench.NoiseFilterManager.Instance != null && PointCloudWorkbench.NoiseFilterManager.Instance.IsPreviewActive)
        {
            posX = 440f; // Shift to the right of the noise legend
        }

        GUILayout.BeginArea(new Rect(posX, posY, width, height), legendStyle);

        GUILayout.Label("🏷 アノテーション分類凡例", legendTitleStyle);
        GUILayout.Space(8);

        foreach (var cls in classes)
        {
            DrawLegendItem(cls.GetColor(), $"{cls.name} ({cls.id})");
        }

        GUILayout.EndArea();
    }

    private void DrawLegendItem(Color color, string label)
    {
        GUILayout.BeginHorizontal();
        
        Rect rect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
        Color oldColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, colorTexture);
        GUI.color = oldColor;

        GUILayout.Space(10);
        GUILayout.Label(label, legendTextStyle);
        
        GUILayout.EndHorizontal();
        GUILayout.Space(3);
    }

    void OnDestroy()
    {
        if (legendBgTexture != null) Destroy(legendBgTexture);
        if (colorTexture != null) Destroy(colorTexture);
    }
}
