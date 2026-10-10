using UnityEngine;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PointCloudWorkbench;

public class PointCloudManager : MonoBehaviour
{
    public Rect LastPanelRect { get; private set; }
    private PointCloudWorkbench.PointCloudOperation activeCompareOperation;
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
    private float maxDistanceThreshold = 1000.0f;
    private float[] calculatedDistances;
    private bool hasCompared = false;
    private bool compareInProgress;

    private struct C2CResult
    {
        public float[] Distances;
        public float Average;
        public float Maximum;
    }

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

    void Start()
    {
        editorUIInstance = Object.FindAnyObjectByType<PointCloudEditorUI>();

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
        PointCloudProgressSnapshot progress = PointCloudProgressManager.Instance.GetSnapshot();
        if (progress.IsRunning || progress.HasError || progress.HasWarning) return;

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
        if (editorUIInstance != null && (editorUIInstance.IsMouseOverUI() || GUIUtility.keyboardControl != 0)) return;

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

        // Use rendered positions so the display-only scale is included in alignment.
        Vector3 refCenter = Vector3.zero;
        foreach (var p in refPos)
        {
            refCenter += referenceCloud.DisplayTransform.TransformPoint(p);
        }
        refCenter /= refPos.Length;

        // Calculate world space center of Aligned Cloud
        Vector3 alignCenter = Vector3.zero;
        foreach (var p in alignPos)
        {
            alignCenter += alignedCloud.DisplayTransform.TransformPoint(p);
        }
        alignCenter /= alignPos.Length;

        // Offset aligned object by the difference
        Vector3 offset = refCenter - alignCenter;
        alignedCloud.transform.position += offset;

        Debug.Log($"[PointCloudManager] Center alignment complete. Offset applied: {offset}");
    }

    // Exact Cloud-to-Cloud nearest-neighbor distance calculation in displayed millimeters.
    public async void CompareClouds()
    {
        if (compareInProgress) return;
        if (referenceCloud == null || alignedCloud == null) return;

        Vector3[] refPos = referenceCloud.GetPositions();
        Vector3[] alignPos = alignedCloud.GetPositions();

        if (refPos == null || alignPos == null || refPos.Length == 0 || alignPos.Length == 0)
        {
            const string message = "C2C比較には基準点群と位置合わせ済み点群の両方が必要です。";
            PointCloudProgressManager.Instance.ShowError("C2C比較", message);
            Debug.LogWarning($"[RecoverableOperationError] {message}");
            return;
        }

        PointCloudProgressManager progress = PointCloudProgressManager.Instance;
        PointCloudWorkbench.PointCloudOperation operation = progress.TryStart("C2C距離計算", "表示座標を準備しています...");
        if (operation == null) return;
        activeCompareOperation = operation;
        compareInProgress = true;
        PointCloudRenderer referenceSnapshot = referenceCloud;
        PointCloudRenderer alignedSnapshot = alignedCloud;
        CancellationToken cancellationToken = operation.CancellationToken;
        try
        {
            PointCloudPoint3[] referencePoints = new PointCloudPoint3[refPos.Length];
            PointCloudPoint3[] alignedPoints = new PointCloudPoint3[alignPos.Length];

            // DisplayTransform includes the existing display scale; distances remain in the UI's mm convention.
            for (int i = 0; i < refPos.Length; i++)
            {
                if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                Vector3 p = referenceSnapshot.DisplayTransform.TransformPoint(refPos[i]);
                referencePoints[i] = new PointCloudPoint3(p.x, p.y, p.z);
            }
            for (int i = 0; i < alignPos.Length; i++)
            {
                if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                Vector3 p = alignedSnapshot.DisplayTransform.TransformPoint(alignPos[i]);
                alignedPoints[i] = new PointCloudPoint3(p.x, p.y, p.z);
            }

            operation.Update(0.2f, "厳密最近傍距離を計算しています...");
            C2CResult result = await Task.Run(
                () => CalculateExactDistances(referencePoints, alignedPoints, cancellationToken, operation),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (referenceCloud != referenceSnapshot || alignedCloud != alignedSnapshot)
            {
                operation.CompleteCancelled("計算中に比較対象が切り替わったため、結果を破棄しました。");
                return;
            }

            calculatedDistances = result.Distances;
            comparedPointCount = result.Distances.Length;
            avgDistance = result.Average;
            maxDistance = result.Maximum;
            hasCompared = true;

            Debug.Log($"[PointCloudManager] Exact C2C calculation complete. Avg Distance: {avgDistance:F1} mm, Max Distance: {maxDistance:F1} mm");
            UpdateColors();
            alignedCloud.ShowDistanceMap(calculatedDistances, maxDistanceThreshold);
            operation.Complete();
        }
        catch (System.OperationCanceledException)
        {
            operation.CompleteCancelled();
        }
        catch (System.Exception ex)
        {
            const string message = "C2C距離の計算に失敗しました。点群と表示変換を確認してください。";
            operation.Fail("C2C比較", message, ex.ToString());
            Debug.LogWarning($"[RecoverableOperationError] {message}\n{ex}");
        }
        finally
        {
            compareInProgress = false;
            if (ReferenceEquals(activeCompareOperation, operation)) activeCompareOperation = null;
        }
    }

    private static C2CResult CalculateExactDistances(PointCloudPoint3[] referencePoints,
        PointCloudPoint3[] alignedPoints, CancellationToken cancellationToken, PointCloudWorkbench.PointCloudOperation operation)
    {
        ExactNearestNeighbor3D nearestNeighbor = new ExactNearestNeighbor3D(referencePoints, cancellationToken);
        float[] distances = new float[alignedPoints.Length];
        float sum = 0f;
        float maximum = 0f;
        int reportInterval = System.Math.Max(1, alignedPoints.Length / 100);
        for (int i = 0; i < alignedPoints.Length; i++)
        {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            nearestNeighbor.FindNearest(alignedPoints[i], out double distanceSquared);
            float distance = (float)System.Math.Sqrt(distanceSquared);
            if (float.IsNaN(distance) || float.IsInfinity(distance))
                throw new System.InvalidOperationException("最近傍距離が有限値ではありません。");
            distances[i] = distance;
            sum += distance;
            if (distance > maximum) maximum = distance;
            if (i % reportInterval == 0)
                operation.Update(0.2f + 0.78f * (i + 1f) / alignedPoints.Length, "厳密最近傍距離を計算しています...");
        }
        return new C2CResult { Distances = distances, Average = sum / alignedPoints.Length, Maximum = maximum };
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
                alignedCloud.transform.localScale = Vector3.one;
            }
        }
    }

    private void InitializeStyles()
    {
        if (stylesInitialized) return;

        // Custom premium dark theme styling for OnGUI
        Texture2D bgTexture = new Texture2D(1, 1);
        bgTexture.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.16f, 0.98f));
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
        textStyle.wordWrap = true;
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
        PointCloudProgressSnapshot activeProgress = PointCloudProgressManager.Instance.GetSnapshot();
        if (activeProgress.HasError || activeProgress.HasWarning) return;

        // 画面幅に応じてパネル幅を動的に決定（最大460、画面幅の25%を超えない）
        float width = Mathf.Min(460f, Screen.width * 0.25f);
        float height = Mathf.Min(930f, Screen.height - 40f);
        float posX = Screen.width - width - 20f;
        float posY = 20f;
        LastPanelRect = new Rect(posX, posY, width, height);
        bool compactLayout = width < 340f;
        headerStyle.fontSize = compactLayout ? 15 : 20;
        buttonStyle.fontSize = compactLayout ? 12 : 14;
        activeButtonStyle.fontSize = buttonStyle.fontSize;
        foldoutHeaderStyle.fontSize = compactLayout ? 12 : 14;
        textStyle.fontSize = compactLayout ? 11 : 13;
        toggleStyle.fontSize = compactLayout ? 12 : 14;
        buttonStyle.wordWrap = compactLayout;
        activeButtonStyle.wordWrap = compactLayout;
        foldoutHeaderStyle.wordWrap = compactLayout;

        if (activeProgress.IsRunning)
        {
            bool previousGuiEnabled = GUI.enabled;
            GUI.enabled = false;
            GUI.Box(LastPanelRect, GUIContent.none, windowStyle);
            GUILayout.BeginArea(LastPanelRect);
            GUILayout.Label("処理中", headerStyle);
            GUILayout.Label(activeProgress.Title, textStyle);
            GUILayout.Label(activeProgress.StatusMessage, textStyle);
            GUILayout.EndArea();
            GUI.enabled = previousGuiEnabled;
            return;
        }
 
        GUILayout.BeginArea(LastPanelRect, windowStyle);
 
        GUILayout.Label(compactLayout ? "点群比較パネル" : "CloudCompare Unity機能パネル", headerStyle);
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
        GUILayout.Label("操作モード", textStyle);
        if (compactLayout)
        {
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
        }
        else
        {
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
        }
        GUILayout.Label("ヒント: [Tab] キーでカメラ操作と点群操作を切り替えられます。", textStyle);
        GUILayout.Space(15);

        // --- 2. Color Map / Scalar Fields Mode ---
        GUILayout.Label("カラー表示モード", textStyle);
        if (compactLayout)
        {
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
        }
        else
        {
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
        }
        GUILayout.Space(15);

        // --- 3. Rendering Adjustments ---
        GUILayout.Label($"点のサイズ: {pointSize:F0}", textStyle);
        float newSize = GUILayout.HorizontalSlider(pointSize, 1.0f, 20.0f);
        if (Mathf.Abs(newSize - pointSize) > 0.1f)
        {
            pointSize = Mathf.Round(newSize);
            if (referenceCloud != null) referenceCloud.SetPointSize(pointSize);
            if (alignedCloud != null) alignedCloud.SetPointSize(pointSize);
        }
        GUILayout.Space(10);

        // --- 4. Alignment Tools ---
        GUILayout.Label("位置合わせ ＆ ICP ツール", textStyle);
        if (compactLayout)
        {
            if (GUILayout.Button("中心位置を合わせる", buttonStyle)) AlignCenters();
            if (GUILayout.Button("位置リセット", buttonStyle)) ResetAlignedPosition();
        }
        else
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("中心位置を合わせる", buttonStyle)) AlignCenters();
            if (GUILayout.Button("位置リセット", buttonStyle)) ResetAlignedPosition();
            GUILayout.EndHorizontal();
        }
        GUILayout.Space(15);

        // --- 5. Analysis / Cloud-to-Cloud Distance Comparison ---
        GUILayout.Label("変化検出 ＆ C2C 距離計算", textStyle);
        bool comparisonTargetsAvailable = referenceCloud != null && alignedCloud != null;
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && comparisonTargetsAvailable && !compareInProgress && !PointCloudProgressManager.Instance.IsRunning;
        if (GUILayout.Button(compareInProgress ? "C2C距離を計算中..." : "C2C 距離計算を実行", activeButtonStyle))
        {
            CompareClouds();
        }
        GUI.enabled = previousEnabled;
        if (!comparisonTargetsAvailable)
            GUILayout.Label("C2Cには比較用の基準点群と比較点群が必要です。", textStyle);
        else if (PointCloudProgressManager.Instance.IsRunning && !compareInProgress)
            GUILayout.Label("別の処理が終了すると実行できます。", textStyle);
        GUILayout.Space(10);

        GUILayout.Label($"C2C カラーしきい値: {maxDistanceThreshold:F0} mm", textStyle);
        float newThreshold = GUILayout.HorizontalSlider(maxDistanceThreshold, 50f, 5000f);
        if (Mathf.Abs(newThreshold - maxDistanceThreshold) > 1f)
        {
            maxDistanceThreshold = newThreshold;
        }
        GUILayout.Space(15);

        // --- 6. Analytics Stats Window ---
        GUILayout.Box("", GUILayout.Height(2)); // Separator line
        GUILayout.Label("C2C 比較統計結果", textStyle);
        if (hasCompared)
        {
            GUILayout.Label($"比較対象点数: {comparedPointCount:N0}", textStyle);
            GUILayout.Label($"平均距離偏差: {avgDistance:F1} mm", textStyle);
            GUILayout.Label($"最大距離偏差: {maxDistance:F1} mm", textStyle);
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

            GUILayout.Label("ツールパネル表示", textStyle);
            bool prevAnn = editorUIInstance.showAnnotationUI;
            bool prevNoise = editorUIInstance.showNoiseFilterUI;
            bool prevMeas = editorUIInstance.showMeasurementUI;
            bool prevStem = editorUIInstance.showStemDiameterUI;
            if (compactLayout)
            {
                editorUIInstance.showAnnotationUI = GUILayout.Toggle(editorUIInstance.showAnnotationUI, "アノテーションUI", toggleStyle);
                editorUIInstance.showNoiseFilterUI = GUILayout.Toggle(editorUIInstance.showNoiseFilterUI, "モヤ処理UI", toggleStyle);
                editorUIInstance.showMeasurementUI = GUILayout.Toggle(editorUIInstance.showMeasurementUI, "距離計測UI", toggleStyle);
                editorUIInstance.showStemDiameterUI = GUILayout.Toggle(editorUIInstance.showStemDiameterUI, "茎径プロファイルUI", toggleStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                editorUIInstance.showAnnotationUI = GUILayout.Toggle(editorUIInstance.showAnnotationUI, " アノテーションUI", toggleStyle);
                editorUIInstance.showNoiseFilterUI = GUILayout.Toggle(editorUIInstance.showNoiseFilterUI, " モヤ処理UI", toggleStyle);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                editorUIInstance.showMeasurementUI = GUILayout.Toggle(editorUIInstance.showMeasurementUI, " 距離計測UI", toggleStyle);
                editorUIInstance.showStemDiameterUI = GUILayout.Toggle(editorUIInstance.showStemDiameterUI, " 茎径プロファイルUI", toggleStyle);
                GUILayout.EndHorizontal();
            }

            if (editorUIInstance.showAnnotationUI != prevAnn || 
                editorUIInstance.showNoiseFilterUI != prevNoise || 
                editorUIInstance.showMeasurementUI != prevMeas ||
                editorUIInstance.showStemDiameterUI != prevStem)
            {
                editorUIInstance.SaveSettings();
            }
            GUILayout.Space(15);
            GUILayout.Box("", GUILayout.Height(1));
            GUILayout.Space(10);

            // Extension Buttons
            GUILayout.Label("スケール校正とダウンサンプリング", textStyle);
            GUILayout.Space(10);

            if (GUILayout.Button("リファレンス球直径を自動推定", activeButtonStyle, GUILayout.Height(45)))
            {
                editorUIInstance.OpenReferenceSphereDialog();
            }
            GUILayout.Space(8);

            if (GUILayout.Button("スケール校正を実行 (基準球実寸設定)", activeButtonStyle, GUILayout.Height(45)))
            {
                editorUIInstance.showScaleCalibDialog = true;
                editorUIInstance.showDownsampleDialog = false;
            }
            GUILayout.Space(10);



            if (GUILayout.Button("ダウンサンプリング処理実行", activeButtonStyle, GUILayout.Height(45)))
            {
                editorUIInstance.showDownsampleDialog = true;
                editorUIInstance.showScaleCalibDialog = false;
            }
            GUILayout.Space(15);
        }

        // --- 7. Ported: LOD & Culling settings (Foldout) ---
        if (editorInstance != null && editorInstance.targetRenderer != null)
        {
            foldoutLOD = GUILayout.Toggle(foldoutLOD, (foldoutLOD ? "▼ " : "▶ ") + "レンダリング最適化", foldoutHeaderStyle);
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
                    GUILayout.Label("オクトリー構築完了 (LOD有効)", textStyle);
                }
                GUILayout.Space(8);
            }
        }

        // --- 8. Ported: Dataset Statistics (Foldout) ---
        if (editorInstance != null && editorInstance.targetRenderer != null)
        {
            int totalPoints = editorInstance.targetRenderer.GetPointData() != null ? editorInstance.targetRenderer.GetPointData().Length : 0;
            foldoutStats = GUILayout.Toggle(foldoutStats, (foldoutStats ? "▼ " : "▶ ") + "データセット統計", foldoutHeaderStyle);
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
        legendBgTexture.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.16f, 0.98f));
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

        GUILayout.Label("アノテーション分類凡例", legendTitleStyle);
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
        activeCompareOperation?.Cancel();
        if (legendBgTexture != null) Destroy(legendBgTexture);
        if (colorTexture != null) Destroy(colorTexture);
    }
}
