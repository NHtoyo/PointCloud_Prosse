using UnityEngine;
using System.IO;
using System.Collections.Generic;
using PointCloudWorkbench;

[RequireComponent(typeof(PointCloudEditor))]
public class PointCloudEditorUI : MonoBehaviour
{
    private PointCloudEditor editor;

    // GUI Styles
    private GUIStyle windowStyle;
    private GUIStyle headerStyle;
    private GUIStyle foldoutHeaderStyle;
    private GUIStyle buttonStyle;
    private GUIStyle activeButtonStyle;
    private GUIStyle textStyle;
    private GUIStyle toggleStyle;
    private bool stylesInitialized = false;

    private Vector2 fileScrollPos;
    private Vector2 errorScrollPos = Vector2.zero;
    private string[] availablePlyFiles = new string[0];
    private float fileCheckTimer = 0f;

    // UI Scroll Position
    private Vector2 mainScrollPos;

    // Foldout Statuses
    private bool foldoutRansac = false;
    private bool foldoutFilter = false;
    private bool foldoutOperations = true;
    private bool foldoutLoad = true;

    // Export Dialog Status
    private bool showExportDialog = false;
    private bool exportOnlySelected = false;
    private Rect exportDialogRect = new Rect(0, 0, 320, 160);

    private NoiseFilterUI noiseFilterUI;
    private FilterPipelineEditorUI pipelineEditorUI;
    private AnnotationPipelineEditorUI annotationPipelineEditorUI;
    private DistanceMeasurementUI distanceMeasurementUI;

    // UI Toggle states
    public bool showNoiseFilterUI = false;
    public bool showAnnotationUI = true;
    private string newLayerName = "NewLayer";

    // Lasso drawing texture
    private Texture2D lineTex;

    // Progress Modal textures
    private Texture2D modalBackdropTex;
    private Texture2D progressBgTex;

    // --- Scale Calibration / Downsampling Modals & Variables ---
    public bool showMeasurementUI = true;
    public bool showScaleCalibDialog = false; // kept for backward compatibility/stubs
    private Rect scaleCalibDialogRect = new Rect(0, 0, 420, 260);
    public bool showDownsampleDialog = false;
    private Rect downsampleDialogRect = new Rect(0, 0, 480, 360);

    public string scaleRealDiameterStr = "60";
    public string scaleMeasurementsStr = "";
    private string downsampleVoxelSizeStr = "5.0";
    private int downsampleMode = 1;
    private string downsampleInputDir = "../PointCloudData";
    private string downsampleOutputDir = "../PointCloudData/downsample";

    // Async execution flags
    private volatile bool scaleFinishedFlag = false;
    private volatile bool scaleFailedFlag = false;
    private volatile string scaleErrorMessage = "";

    private volatile bool downsampleFinishedFlag = false;
    private volatile bool downsampleFailedFlag = false;
    private volatile string downsampleErrorMessage = "";

    public void LoadSettings()
    {
        scaleRealDiameterStr = PlayerPrefs.GetString("ScaleCalib_RealDiameterStr", "60");
        scaleMeasurementsStr = PlayerPrefs.GetString("ScaleCalib_Measurements", "");
        downsampleMode = PlayerPrefs.GetInt("Downsample_Mode", 1);
        downsampleVoxelSizeStr = PlayerPrefs.GetString("Downsample_VoxelSizeStr", "5.0");
        downsampleInputDir = PlayerPrefs.GetString("Downsample_InputDir", "../PointCloudData");
        downsampleOutputDir = PlayerPrefs.GetString("Downsample_OutputDir", "../PointCloudData/downsample");

        showNoiseFilterUI = PlayerPrefs.GetInt("Show_NoiseFilterUI", 0) == 1;
        showAnnotationUI = PlayerPrefs.GetInt("Show_AnnotationUI", 1) == 1;
        showMeasurementUI = PlayerPrefs.GetInt("Show_MeasurementUI", 1) == 1;
    }

    public void SaveSettings()
    {
        PlayerPrefs.SetString("ScaleCalib_RealDiameterStr", scaleRealDiameterStr);
        PlayerPrefs.SetString("ScaleCalib_Measurements", scaleMeasurementsStr);
        PlayerPrefs.SetInt("Downsample_Mode", downsampleMode);
        PlayerPrefs.SetString("Downsample_VoxelSizeStr", downsampleVoxelSizeStr);
        PlayerPrefs.SetString("Downsample_InputDir", downsampleInputDir);
        PlayerPrefs.SetString("Downsample_OutputDir", downsampleOutputDir);

        PlayerPrefs.SetInt("Show_NoiseFilterUI", showNoiseFilterUI ? 1 : 0);
        PlayerPrefs.SetInt("Show_AnnotationUI", showAnnotationUI ? 1 : 0);
        PlayerPrefs.SetInt("Show_MeasurementUI", showMeasurementUI ? 1 : 0);
        PlayerPrefs.Save();
    }

    void Start()
    {
        editor = GetComponent<PointCloudEditor>();
        noiseFilterUI = GetComponent<NoiseFilterUI>();
        if (noiseFilterUI == null)
        {
            noiseFilterUI = gameObject.AddComponent<NoiseFilterUI>();
        }
        pipelineEditorUI = GetComponent<FilterPipelineEditorUI>();
        if (pipelineEditorUI == null)
        {
            pipelineEditorUI = gameObject.AddComponent<FilterPipelineEditorUI>();
        }
        annotationPipelineEditorUI = GetComponent<AnnotationPipelineEditorUI>();
        if (annotationPipelineEditorUI == null)
        {
            annotationPipelineEditorUI = gameObject.AddComponent<AnnotationPipelineEditorUI>();
        }
        distanceMeasurementUI = GetComponent<DistanceMeasurementUI>();
        if (distanceMeasurementUI == null)
        {
            distanceMeasurementUI = gameObject.AddComponent<DistanceMeasurementUI>();
        }
        LoadSettings();
        RefreshFileList();
    }

    void Update()
    {
        // 日本語IME入力を有効化
        if (Input.imeCompositionMode != IMECompositionMode.On)
        {
            Input.imeCompositionMode = IMECompositionMode.On;
        }

        // 定期的なファイルリストの更新
        fileCheckTimer -= Time.deltaTime;
        if (fileCheckTimer <= 0f)
        {
            RefreshFileList();
            fileCheckTimer = 2.0f;
        }

        // スケール校正とダウンサンプリングの非同期結果チェック
        if (scaleFinishedFlag)
        {
            scaleFinishedFlag = false;
            PointCloudProgressManager.Instance.Complete();
            UnityEngine.Debug.Log("スケール校正処理が正常に完了しました。");
            
            // スケール校正完了後に、Transformスケールへ即座に反映する
            PointCloudManager manager = Object.FindAnyObjectByType<PointCloudManager>();
            if (manager != null)
            {
                manager.ApplyScaleCalibration();
            }
        }
        if (scaleFailedFlag)
        {
            scaleFailedFlag = false;
            PointCloudProgressManager.Instance.ShowError("スケール校正エラー", scaleErrorMessage);
        }

        if (downsampleFinishedFlag)
        {
            downsampleFinishedFlag = false;
            PointCloudProgressManager.Instance.Complete();
            UnityEngine.Debug.Log("ダウンサンプリング処理が正常に完了しました。");

            // ダウンサンプリングされた全体ファイルを自動で再ロードして表示を更新
            if (editor != null && editor.targetRenderer != null)
            {
                var loader = editor.targetRenderer.GetComponent<PointCloudLoader>();
                if (loader != null)
                {
                    DownsamplePaths paths = PointCloudDownsampleService.BuildPaths(loader.GetFilePath());
                    string downsampledPath = paths.CombinedOutputPath;

                    if (System.IO.File.Exists(downsampledPath))
                    {
                        UnityEngine.Debug.Log($"[Downsample Auto-Load] Loading downsampled PLY: {downsampledPath}");
                        
                        loader.fileName = PointCloudDownsampleService.GetLoaderRelativePath(downsampledPath);
                        loader.LoadPointCloud(downsampledPath);
                        
                        // カメラを再センタリング
                        var camCtrl = Object.FindAnyObjectByType<CloudCompareCameraController>();
                        if (camCtrl != null)
                        {
                            camCtrl.CenterOnRenderer(editor.targetRenderer);
                        }
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning($"[Downsample Auto-Load] Downsampled file not found: {downsampledPath} (Mode 2 Per-Organ only?)");
                    }
                }
            }
        }
        if (downsampleFailedFlag)
        {
            downsampleFailedFlag = false;
            PointCloudProgressManager.Instance.ShowError("ダウンサンプリングエラー", downsampleErrorMessage);
        }
    }

    private void RefreshFileList()
    {
        if (editor == null || editor.targetRenderer == null) return;
        var loader = editor.targetRenderer.GetComponent<PointCloudLoader>();
        if (loader != null)
        {
            string folder = loader.useExternalPath ? loader.externalFolderPath : Application.streamingAssetsPath;
            if (Directory.Exists(folder))
            {
                availablePlyFiles = Directory.GetFiles(folder, "*.ply");
            }
            else
            {
                availablePlyFiles = new string[0];
            }
        }
    }

    private void InitializeStyles()
    {
        if (stylesInitialized) return;

        Texture2D bgTexture = new Texture2D(1, 1);
        bgTexture.SetPixel(0, 0, new Color(0.08f, 0.1f, 0.12f, 0.96f)); // Sleek professional dark
        bgTexture.Apply();

        windowStyle = new GUIStyle(GUI.skin.box);
        windowStyle.normal.background = bgTexture;
        windowStyle.padding = new RectOffset(16, 16, 16, 16);

        headerStyle = new GUIStyle();
        headerStyle.fontSize = 22; // Enlarge from 18
        headerStyle.fontStyle = FontStyle.Bold;
        headerStyle.normal.textColor = new Color(0.15f, 0.76f, 1f); // Vibrant light blue
        headerStyle.alignment = TextAnchor.MiddleCenter;
        headerStyle.margin = new RectOffset(0, 0, 0, 12);

        foldoutHeaderStyle = new GUIStyle(GUI.skin.button);
        foldoutHeaderStyle.fontSize = 15; // Enlarge from 12
        foldoutHeaderStyle.fontStyle = FontStyle.Bold;
        foldoutHeaderStyle.alignment = TextAnchor.MiddleLeft;
        foldoutHeaderStyle.normal.textColor = Color.white;
        foldoutHeaderStyle.padding = new RectOffset(10, 10, 6, 6);
        foldoutHeaderStyle.margin = new RectOffset(0, 0, 5, 5);
        
        Texture2D foldoutBg = new Texture2D(1, 1);
        foldoutBg.SetPixel(0, 0, new Color(0.18f, 0.22f, 0.26f, 0.9f));
        foldoutBg.Apply();
        foldoutHeaderStyle.normal.background = foldoutBg;

        buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.fontSize = 14; // Enlarge from 12
        buttonStyle.fontStyle = FontStyle.Bold;
        buttonStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
        buttonStyle.hover.textColor = Color.white;
        buttonStyle.padding = new RectOffset(8, 8, 6, 6);
        buttonStyle.margin = new RectOffset(2, 2, 2, 2);

        activeButtonStyle = new GUIStyle(buttonStyle);
        Texture2D activeBg = new Texture2D(1, 1);
        activeBg.SetPixel(0, 0, new Color(0.1f, 0.55f, 0.28f, 1f)); // Harmonious Emerald Green
        activeBg.Apply();
        activeButtonStyle.normal.background = activeBg;
        activeButtonStyle.normal.textColor = Color.white;

        textStyle = new GUIStyle(GUI.skin.label);
        textStyle.fontSize = 14; // Enlarge from 12
        textStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
        textStyle.margin = new RectOffset(0, 0, 3, 3);

        toggleStyle = new GUIStyle(GUI.skin.toggle);
        toggleStyle.fontSize = 14;
        toggleStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
        toggleStyle.hover.textColor = Color.white;
        toggleStyle.margin = new RectOffset(0, 0, 3, 3);

        modalBackdropTex = new Texture2D(1, 1);
        modalBackdropTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.65f)); // Semi-transparent black backdrop
        modalBackdropTex.Apply();

        progressBgTex = new Texture2D(1, 1);
        progressBgTex.SetPixel(0, 0, new Color(0.12f, 0.15f, 0.18f, 1f)); // Dark slate grey for bar container
        progressBgTex.Apply();

        stylesInitialized = true;
    }

    public bool IsMouseOverUI()
    {
        // Block mouse interactions if modal progress dialog is running or parameters dialogs are open
        if (PointCloudProgressManager.Instance.IsRunning || showDownsampleDialog || showExportDialog) return true;

        float mouseX = Input.mousePosition.x;
        float mouseY = Input.mousePosition.y;
        
        // Pipeline bar: X=490〜(Screen.width-480), Y=15〜315
        bool overPipelineBar = (mouseX >= 490f && mouseX <= Screen.width - 480f
                             && mouseY >= Screen.height - 315f && mouseY <= Screen.height - 15f);
        
        // Shipped panel bounds (Left Panel: 460w/930h, Right Panel: 460w/930h)
        bool overLeftUI = (mouseX >= 10f && mouseX <= 480f && mouseY >= (Screen.height - 950f) && mouseY <= (Screen.height - 20f));
        bool overRightUI = (mouseX >= Screen.width - 480f && mouseX <= Screen.width - 10f && mouseY >= (Screen.height - 950f) && mouseY <= (Screen.height - 20f));
        return overPipelineBar || overLeftUI || overRightUI;
    }

    void OnGUI()
    {
        if (editor == null || editor.targetRenderer == null) return;
        InitializeStyles();

        PointData[] points = editor.targetRenderer.GetPointData();
        int totalPoints = points != null ? points.Length : 0;

        float width = 460f;
        float height = 930f;
        float posX = 20f;
        float posY = 20f;

        GUILayout.BeginArea(new Rect(posX, posY, width, height), windowStyle);

        GUILayout.Label("🛠 植物点群アノテーションパネル", headerStyle);
        GUILayout.Box("", GUILayout.Height(2));
        GUILayout.Space(5);

        // Scrollview to fit everything cleanly
        mainScrollPos = GUILayout.BeginScrollView(mainScrollPos, GUILayout.Width(width - 15), GUILayout.Height(height - 40));

        // --- 1. Tool Selection ---
        GUILayout.Label("🔧 操作ツール選択 (基本ツール)", textStyle);
        
        // Row 1
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("なし (カメラ操作)", editor.activeTool == PointCloudEditor.EditTool.None ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 3f)))
        {
            editor.activeTool = PointCloudEditor.EditTool.None;
        }
        if (GUILayout.Button("3Dブラシ", editor.activeTool == PointCloudEditor.EditTool.Brush ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 3f)))
        {
            editor.activeTool = PointCloudEditor.EditTool.Brush;
        }
        if (GUILayout.Button("2D矩形選択", editor.activeTool == PointCloudEditor.EditTool.Marquee ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 3f)))
        {
            editor.activeTool = PointCloudEditor.EditTool.Marquee;
        }
        GUILayout.EndHorizontal();

        // Row 2
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("なげなわ多角形選択", editor.activeTool == PointCloudEditor.EditTool.Lasso ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 30) / 2f)))
        {
            editor.activeTool = PointCloudEditor.EditTool.Lasso;
        }
        if (GUILayout.Button("接続探索選択", editor.activeTool == PointCloudEditor.EditTool.Connect ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 30) / 2f)))
        {
            editor.activeTool = PointCloudEditor.EditTool.Connect;
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(5);

        // --- 2. Tool Configurations ---
        if (editor.activeTool == PointCloudEditor.EditTool.Brush)
        {
            GUILayout.Label($"🖌 ブラシ半径: {editor.brushRadius:F2} m", textStyle);
            editor.brushRadius = GUILayout.HorizontalSlider(editor.brushRadius, 0.02f, 0.2f);
            GUILayout.Label("ヒント: [Alt] + ホイールでブラシ半径を変更できます。", textStyle);
            GUILayout.Space(5);
        }
        else if (editor.activeTool == PointCloudEditor.EditTool.Lasso)
        {
            GUILayout.Label("📝 なげなわ多角形選択の操作方法:", textStyle);
            GUILayout.Label("  - 画面上をクリックして頂点追加", textStyle);
            GUILayout.Label($"  - 現在の頂点数: {editor.LassoPoints.Count}", textStyle);
            GUILayout.Label("  - [Enter] または [右クリック] で多角形を閉じ、選択適用", textStyle);
            GUILayout.Space(5);
        }
        else if (editor.activeTool == PointCloudEditor.EditTool.Connect)
        {
            GUILayout.Label("🌀 空間近接（接続探索）設定", textStyle);
            editor.connectionRadius = Mathf.Clamp(editor.connectionRadius, 0.00005f, 0.02f);
            GUILayout.Label($"  接続しきい値 (距離): {editor.connectionRadius:F5} m", textStyle);
            editor.connectionRadius = GUILayout.HorizontalSlider(editor.connectionRadius, 0.00005f, 0.02f);

            GUILayout.Label($"  最大接続制限点数: {editor.maxConnectionPoints:N0} 点", textStyle);
            
            // 対数スライダー（1,000 点 〜 5,000,000 点）
            float logMin = Mathf.Log10(1000f);
            float logMax = Mathf.Log10(5000000f);
            float currentVal = Mathf.Clamp(editor.maxConnectionPoints, 1000f, 5000000f);
            float t = (Mathf.Log10(currentVal) - logMin) / (logMax - logMin);
            
            t = GUILayout.HorizontalSlider(t, 0f, 1f);
            
            float rawVal = Mathf.Pow(10f, logMin + t * (logMax - logMin));
            
            // キリの良い値に段階的に丸める
            int roundedVal;
            if (rawVal < 10000f)
            {
                roundedVal = Mathf.RoundToInt(rawVal / 1000f) * 1000;
            }
            else if (rawVal < 100000f)
            {
                roundedVal = Mathf.RoundToInt(rawVal / 5000f) * 5000;
            }
            else if (rawVal < 1000000f)
            {
                roundedVal = Mathf.RoundToInt(rawVal / 50000f) * 50000;
            }
            else
            {
                roundedVal = Mathf.RoundToInt(rawVal / 100000f) * 100000;
            }
            
            editor.maxConnectionPoints = Mathf.Clamp(roundedVal, 1000, 5000000);
            
            GUILayout.Label("操作方法: 点群の任意の点を選択すると、隣接する点が自動追跡選択されます。", textStyle);
            GUILayout.Space(5);
        }

        // --- 3. Selection Mode (Select vs Deselect) ---
        if (editor.activeTool != PointCloudEditor.EditTool.None)
        {
            GUILayout.Label("⚡ 選択・解除 挙動", textStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("選択 (追加)", editor.brushSelectMode ? activeButtonStyle : buttonStyle))
            {
                editor.brushSelectMode = true;
            }
            if (GUILayout.Button("選択解除 (削除)", !editor.brushSelectMode ? activeButtonStyle : buttonStyle))
            {
                editor.brushSelectMode = false;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
        }

        // --- 4. RANSAC Fitting (Foldout) ---
        foldoutRansac = GUILayout.Toggle(foldoutRansac, (foldoutRansac ? "▼ " : "▶ ") + "📐 幾何形状検出 (RANSACフィット)", foldoutHeaderStyle);
        if (foldoutRansac)
        {
            GUILayout.Space(3);
            GUILayout.Label("対象の幾何形状:", textStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("平面 (床・壁)", editor.ransacType == PointCloudEditor.RansacType.Plane ? activeButtonStyle : buttonStyle))
            {
                editor.ransacType = PointCloudEditor.RansacType.Plane;
            }
            if (GUILayout.Button("鉛直円柱", editor.ransacType == PointCloudEditor.RansacType.Cylinder ? activeButtonStyle : buttonStyle))
            {
                editor.ransacType = PointCloudEditor.RansacType.Cylinder;
            }
            if (GUILayout.Button("支柱拡張", activeButtonStyle))
            {
                editor.ApplySupportCylinderFromSelection();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label($"RANSAC用 許容誤差: {editor.ransacTolerance * 100f:F1} cm", textStyle);
            editor.ransacTolerance = GUILayout.HorizontalSlider(editor.ransacTolerance, 0.002f, 0.15f);

            GUILayout.Label($"支柱 色許容: {editor.supportColorTolerance:F0}", textStyle);
            editor.supportColorTolerance = GUILayout.HorizontalSlider(editor.supportColorTolerance, 20f, 180f);

            GUILayout.Label($"支柱 太さ倍率: {editor.supportTubeMultiplier:F1}", textStyle);
            editor.supportTubeMultiplier = GUILayout.HorizontalSlider(editor.supportTubeMultiplier, 1.0f, 8.0f);

            if (GUILayout.Button($"🚀 RANSAC 検出を実行 (インライア{(editor.brushSelectMode ? "選択" : "解除")})", activeButtonStyle))
            {
                editor.ApplyRansacSelection();
            }
            GUILayout.Space(8);
        }

        // --- 5. Attribute Filter (Foldout) ---
        foldoutFilter = GUILayout.Toggle(foldoutFilter, (foldoutFilter ? "▼ " : "▶ ") + "🎨 属性・カラー抽出フィルタ", foldoutHeaderStyle);
        if (foldoutFilter)
        {
            GUILayout.Space(3);
            GUILayout.Label("フィルタ属性タイプ:", textStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("高度(Y)", editor.filterType == PointCloudEditor.FilterType.Height ? activeButtonStyle : buttonStyle))
            {
                editor.filterType = PointCloudEditor.FilterType.Height;
                editor.filterMin = -1.5f;
                editor.filterMax = 2.5f;
            }
            if (GUILayout.Button("C2C距離", editor.filterType == PointCloudEditor.FilterType.Distance ? activeButtonStyle : buttonStyle))
            {
                editor.filterType = PointCloudEditor.FilterType.Distance;
                editor.filterMin = 0.0f;
                editor.filterMax = 0.5f;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("赤色度 (実)", editor.filterType == PointCloudEditor.FilterType.Redness ? activeButtonStyle : buttonStyle))
            {
                editor.filterType = PointCloudEditor.FilterType.Redness;
                editor.filterMin = 1.0f;
                editor.filterMax = 3.0f;
            }
            if (GUILayout.Button("緑色度 (葉)", editor.filterType == PointCloudEditor.FilterType.Greenness ? activeButtonStyle : buttonStyle))
            {
                editor.filterType = PointCloudEditor.FilterType.Greenness;
                editor.filterMin = 1.0f;
                editor.filterMax = 3.0f;
            }
            GUILayout.EndHorizontal();

            // Dynamic slider bounds depending on filter type
            float sliderMinLimit = 0f;
            float sliderMaxLimit = 1f;
            string unit = "";
            if (editor.filterType == PointCloudEditor.FilterType.Height)
            {
                sliderMinLimit = -3.0f;
                sliderMaxLimit = 4.0f;
                unit = " m";
            }
            else if (editor.filterType == PointCloudEditor.FilterType.Distance)
            {
                sliderMinLimit = 0.0f;
                sliderMaxLimit = 2.0f;
                unit = " m";
            }
            else if (editor.filterType == PointCloudEditor.FilterType.Redness || editor.filterType == PointCloudEditor.FilterType.Greenness)
            {
                sliderMinLimit = 0.0f;
                sliderMaxLimit = 5.0f;
                unit = " (比率)";
            }

            GUILayout.Label($"  下限値 (Min): {editor.filterMin:F2}{unit}", textStyle);
            editor.filterMin = GUILayout.HorizontalSlider(editor.filterMin, sliderMinLimit, sliderMaxLimit);
            
            GUILayout.Label($"  上限値 (Max): {editor.filterMax:F2}{unit}", textStyle);
            editor.filterMax = GUILayout.HorizontalSlider(editor.filterMax, sliderMinLimit, sliderMaxLimit);

            // Keep min <= max
            if (editor.filterMin > editor.filterMax) editor.filterMin = editor.filterMax;

            if (GUILayout.Button($"🔍 属性フィルタ選択を実行 (範囲内を{(editor.brushSelectMode ? "選択" : "解除")})", activeButtonStyle))
            {
                editor.ApplyAttributeFilterSelection();
            }
            GUILayout.Space(8);
        }

        // --- 6. Operations (Foldout) ---
        foldoutOperations = GUILayout.Toggle(foldoutOperations, (foldoutOperations ? "▼ " : "▶ ") + "✏ 選択オブジェクト操作", foldoutHeaderStyle);
        if (foldoutOperations)
        {
            GUILayout.Space(3);
            
            // 選択点数の表示をここに常時表示
            if (editor.SelectedPointCount > 0)
            {
                GUIStyle countStyle = new GUIStyle(textStyle);
                countStyle.normal.textColor = new Color(0.1f, 0.8f, 0.4f); // 緑ハイライト
                countStyle.fontStyle = FontStyle.Bold;
                GUILayout.Label($"現在の選択点数: {editor.SelectedPointCount:N0} 点", countStyle);
            }
            else
            {
                GUILayout.Label($"現在の選択点数: {editor.SelectedPointCount:N0} 点", textStyle);
            }
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("選択クリア", buttonStyle)) editor.ClearSelection();
            if (GUILayout.Button("選択反転", buttonStyle)) editor.InvertSelection();
            if (GUILayout.Button("選択点を削除", buttonStyle)) editor.DeleteSelected();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("削除した点を復元 (ノイズ除去クリア)", buttonStyle)) editor.RestoreDeleted();
            GUILayout.Space(10);

            // --- Annotation Layer Management ---
            var rend = editor.targetRenderer;
            if (rend != null)
            {
                GUILayout.Box("", GUILayout.Height(1)); // Separator line
                GUILayout.Space(5);
                GUILayout.Label("📑 アノテーションレイヤー管理 (マルチレイヤー)", textStyle);

                List<string> layers = rend.GetAnnotationLayerNames();
                string activeLayer = rend.GetActiveAnnotationLayerName();

                GUILayout.Label($"現在のアクティブレイヤー: {activeLayer}", textStyle);

                GUILayout.BeginHorizontal();
                for (int i = 0; i < layers.Count; i++)
                {
                    string layer = layers[i];
                    bool isActive = layer == activeLayer;
                    if (GUILayout.Button(layer, isActive ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 45) / 3f)))
                    {
                        rend.SwitchAnnotationLayer(layer);
                        editor.MarkStatsDirty();
                    }
                    if ((i + 1) % 3 == 0 && i < layers.Count - 1)
                    {
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                    }
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(5);

                GUILayout.BeginHorizontal();
                newLayerName = GUILayout.TextField(newLayerName, GUILayout.Width(240));
                if (GUILayout.Button("レイヤー追加", buttonStyle, GUILayout.Width(140)))
                {
                    if (!string.IsNullOrEmpty(newLayerName) && !layers.Contains(newLayerName))
                    {
                        rend.AddAnnotationLayer(newLayerName);
                        rend.SwitchAnnotationLayer(newLayerName);
                        editor.MarkStatsDirty();
                        newLayerName = "NewLayer";
                    }
                }
                GUILayout.EndHorizontal();

                if (activeLayer != "Default")
                {
                    if (GUILayout.Button("❌ 現在のアクティブレイヤーを削除", activeButtonStyle))
                    {
                        rend.DeleteAnnotationLayer(activeLayer);
                        editor.MarkStatsDirty();
                    }
                }
            }
            GUILayout.Space(8);
        }


        // foldoutScaleCalib and Scale Calibration settings are completely removed from left panel OnGUI


        // --- 7. Load File Selection (Foldout) ---
        foldoutLoad = GUILayout.Toggle(foldoutLoad, (foldoutLoad ? "▼ " : "▶ ") + "📂 読み込みPLYファイル選択", foldoutHeaderStyle);
        if (foldoutLoad)
        {
            GUILayout.Space(3);
            var loader = editor.targetRenderer.GetComponent<PointCloudLoader>();
            if (loader != null)
            {
                string folder = loader.useExternalPath ? loader.externalFolderPath : Application.streamingAssetsPath;
                if (Directory.Exists(folder))
                {
                    if (availablePlyFiles.Length > 0)
                    {
                        fileScrollPos = GUILayout.BeginScrollView(fileScrollPos, GUILayout.Height(80));
                        for (int i = 0; i < availablePlyFiles.Length; i++)
                        {
                            string fName = Path.GetFileName(availablePlyFiles[i]);
                            bool isCurrent = loader.fileName == fName;
                            GUI.enabled = !isCurrent;
                            if (GUILayout.Button(fName, isCurrent ? activeButtonStyle : buttonStyle))
                            {
                                if (!isCurrent && Time.time > 1.0f)
                                {
                                    loader.fileName = fName;
                                    loader.LoadPointCloud(availablePlyFiles[i]);
                                    var cam = Object.FindAnyObjectByType<CloudCompareCameraController>();
                                    if (cam != null) cam.hasCenteredOnCloud = false;
                                    editor.MarkStatsDirty();
                                }
                            }
                            GUI.enabled = true;
                        }
                        GUILayout.EndScrollView();
                    }
                    else
                    {
                        GUILayout.Label("フォルダ内にPLYファイルが見つかりません。", textStyle);
                    }
                }
                else
                {
                    GUILayout.Label("フォルダが存在しません。", textStyle);
                }
            }
            GUILayout.Space(8);
        }

        // --- 10. PLY Export (Visible even when stats foldout is closed) ---
        GUILayout.Space(8);
        if (GUILayout.Button("💾 PLYをエクスポート", activeButtonStyle))
        {
            exportOnlySelected = false;
            showExportDialog = true;
        }
        GUILayout.Space(5);
        if (GUILayout.Button("💾 選択点のみエクスポート", activeButtonStyle))
        {
            exportOnlySelected = true;
            showExportDialog = true;
        }
        GUILayout.Space(5);

        GUILayout.EndScrollView();
        GUILayout.EndArea();


        // --- 12. Modal Input Dialogs for Scale Calibration / Downsampling ---

        if (showDownsampleDialog)
        {
            downsampleDialogRect.x = (Screen.width - downsampleDialogRect.width) / 2f;
            downsampleDialogRect.y = (Screen.height - downsampleDialogRect.height) / 2f;
            downsampleDialogRect = GUI.Window(997, downsampleDialogRect, DrawDownsampleWindow, "📥 ダウンサンプリングパラメータ設定", windowStyle);
            GUI.BringWindowToFront(997);
        }

        if (showScaleCalibDialog)
        {
            scaleCalibDialogRect.x = (Screen.width - scaleCalibDialogRect.width) / 2f;
            scaleCalibDialogRect.y = (Screen.height - scaleCalibDialogRect.height) / 2f;
            scaleCalibDialogRect = GUI.Window(998, scaleCalibDialogRect, DrawScaleCalibWindow, "📐 スケール校正パラメータ設定", windowStyle);
            GUI.BringWindowToFront(998);
        }

        // Draw 2D Marquee Box on screen if active
        if (editor.activeTool == PointCloudEditor.EditTool.Marquee && editor.IsDrawingMarquee)
        {
            Vector2 start = editor.MarqueeStart;
            Vector2 end = editor.MarqueeEnd;
            start.y = Screen.height - start.y;
            end.y = Screen.height - end.y;

            float x = Mathf.Min(start.x, end.x);
            float y = Mathf.Min(start.y, end.y);
            float w = Mathf.Abs(start.x - end.x);
            float h = Mathf.Abs(start.y - end.y);

            GUI.color = Color.green;
            GUI.Box(new Rect(x, y, w, h), "");
            GUI.color = Color.white; 
        }

        // Draw Lasso lines on screen if active
        DrawLassoLines();

        // --- Draw Chained Pipeline/Annotation/Calibration Windows ---
        float currentCenterY = 15f;
        if (showNoiseFilterUI && pipelineEditorUI != null)
        {
            pipelineEditorUI.DrawGUI(ref currentCenterY);
        }
        if (showAnnotationUI && annotationPipelineEditorUI != null)
        {
            annotationPipelineEditorUI.DrawGUI(ref currentCenterY);
        }
        if (showMeasurementUI && distanceMeasurementUI != null)
        {
            distanceMeasurementUI.DrawGUI(ref currentCenterY);
        }

        // Draw Progress Pop-up Window if running (Modal state)
        var pm = PointCloudProgressManager.Instance;
        if (pm.IsRunning)
        {
            DrawProgressDialog(pm);
        }

        // --- 11. Format Selection Dialog for Export ---
        if (showExportDialog)
        {
            exportDialogRect.x = (Screen.width - exportDialogRect.width) / 2f;
            exportDialogRect.y = (Screen.height - exportDialogRect.height) / 2f;
            string title = exportOnlySelected ? "💾 選択点PLYエクスポート設定" : "💾 PLYエクスポート設定";
            exportDialogRect = GUI.Window(999, exportDialogRect, DrawExportDialogWindow, title, windowStyle);
            GUI.BringWindowToFront(999);
        }
    }

    private void DrawExportDialogWindow(int windowID)
    {
        GUILayout.Space(10);
        GUILayout.Label(" 出力フォーマットを選択してください:", textStyle);
        GUILayout.Space(15);
        
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("ASCII (テキスト)", buttonStyle, GUILayout.Height(35)))
        {
            showExportDialog = false;
            if (exportOnlySelected)
            {
                editor.ExportSelectedPoints(false);
            }
            else
            {
                editor.ExportLabeledPoints(false);
            }
        }
        GUILayout.Space(10);
        if (GUILayout.Button("Binary (バイナリ)", buttonStyle, GUILayout.Height(35)))
        {
            showExportDialog = false;
            if (exportOnlySelected)
            {
                editor.ExportSelectedPoints(true);
            }
            else
            {
                editor.ExportLabeledPoints(true);
            }
        }
        GUILayout.EndHorizontal();
        
        GUILayout.Space(15);
        if (GUILayout.Button("キャンセル", buttonStyle, GUILayout.Height(25)))
        {
            showExportDialog = false;
        }
    }

    private void DrawProgressDialog(PointCloudProgressManager pm)
    {
        Color savedGuiColor = GUI.color;

        // Fullscreen block to prevent clicking items in background
        GUIStyle backdropStyle = new GUIStyle();
        if (modalBackdropTex != null)
        {
            backdropStyle.normal.background = modalBackdropTex;
        }
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "", backdropStyle);

        // Center window coordinates
        float w = pm.IsError ? 600f : 500f; // エラー時は少し広げる
        float h = pm.IsError ? 350f : 210f; // エラー時は縦に広げる
        float x = (Screen.width - w) * 0.5f;
        float y = (Screen.height - h) * 0.5f;

        GUILayout.BeginArea(new Rect(x, y, w, h), windowStyle);
        
        if (pm.IsError)
        {
            GUIStyle errHeaderStyle = new GUIStyle(headerStyle);
            errHeaderStyle.normal.textColor = new Color(1.0f, 0.3f, 0.3f); // 赤色
            GUILayout.Label($"❌ {pm.Title}", errHeaderStyle);
            GUILayout.Space(8);
            
            GUILayout.Label("処理中に以下のエラーが発生しました。ログを確認してください。", textStyle);
            GUILayout.Space(5);

            // エラーログ表示用のスクロールビュー
            errorScrollPos = GUILayout.BeginScrollView(errorScrollPos, GUILayout.Height(180));
            GUIStyle errTextStyle = new GUIStyle(textStyle);
            errTextStyle.normal.textColor = new Color(1.0f, 0.4f, 0.4f); // 薄い赤
            errTextStyle.fontSize = 13;
            GUILayout.TextArea(pm.ErrorMessage, errTextStyle);
            GUILayout.EndScrollView();

            GUILayout.Space(15);
            if (GUILayout.Button("閉じる", activeButtonStyle, GUILayout.Height(35)))
            {
                pm.Complete();
            }
        }
        else
        {
            GUILayout.Label($"⏳ {pm.Title}", headerStyle);
            GUILayout.Space(8);
            
            GUILayout.Label(pm.StatusMessage, textStyle);
            GUILayout.Space(8);

            // Progress bar container
            Rect progressRect = GUILayoutUtility.GetRect(w - 32, 26);
            
            // Progress background
            GUIStyle barBgStyle = new GUIStyle();
            if (progressBgTex != null)
            {
                barBgStyle.normal.background = progressBgTex;
            }
            GUI.Box(progressRect, "", barBgStyle);
            
            // Progress fill (utilizing lineTex or basic texture if null)
            float fillWidth = (progressRect.width - 4) * pm.Progress;
            if (fillWidth > 0.1f)
            {
                GUI.color = new Color(0.15f, 0.76f, 1f, 0.9f); // Cyan fill
                GUI.DrawTexture(new Rect(progressRect.x + 2, progressRect.y + 2, fillWidth, progressRect.height - 4), lineTex != null ? lineTex : Texture2D.whiteTexture);
                GUI.color = Color.white;
            }

            // Progress percentage text
            GUIStyle percentStyle = new GUIStyle(textStyle);
            percentStyle.alignment = TextAnchor.MiddleCenter;
            percentStyle.fontStyle = FontStyle.Bold;
            percentStyle.normal.textColor = Color.white;
            GUI.Label(progressRect, $"{pm.Progress * 100f:F1} %", percentStyle);

            GUILayout.Space(15);
            if (GUILayout.Button("処理をキャンセル", activeButtonStyle, GUILayout.Height(35)))
            {
                pm.Cancel();
            }
        }

        GUILayout.EndArea();
        GUI.color = savedGuiColor;
    }

    private void DrawLine(Vector2 start, Vector2 end, Color color, float width)
    {
        if (lineTex == null)
        {
            lineTex = new Texture2D(1, 1);
            lineTex.SetPixel(0, 0, Color.white);
            lineTex.Apply();
        }
        Color savedColor = GUI.color;
        GUI.color = color;
        Vector2 d = end - start;
        float a = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        GUIUtility.RotateAroundPivot(a, start);
        GUI.DrawTexture(new Rect(start.x, start.y, d.magnitude, width), lineTex);
        GUIUtility.RotateAroundPivot(-a, start);
        GUI.color = savedColor;
    }

    private void DrawLassoLines()
    {
        if (editor == null || editor.activeTool != PointCloudEditor.EditTool.Lasso) return;
        var points = editor.LassoPoints;
        if (points == null || points.Count == 0) return;

        Vector2 prev = Vector2.zero;
        Color lineColor = new Color(0.15f, 0.76f, 1f, 0.9f); // Vibrant light blue
        float lineWidth = 2.5f;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2 curr = new Vector2(points[i].x, Screen.height - points[i].y);
            if (i > 0)
            {
                DrawLine(prev, curr, lineColor, lineWidth);
            }
            prev = curr;
        }

        // Connect to mouse position as helper
        Vector2 mousePos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        DrawLine(prev, mousePos, new Color(1f, 0.9f, 0f, 0.8f), 1.5f); // Yellow helper line
        
        // Connect mouse back to the first point for visual closure preview
        if (points.Count >= 2)
        {
            Vector2 start = new Vector2(points[0].x, Screen.height - points[0].y);
            DrawLine(mousePos, start, new Color(1f, 0.9f, 0f, 0.4f), 1.5f);
        }
    }


    private void DrawDownsampleWindow(int windowID)
    {
        GUILayout.Space(10);
        GUILayout.Label("ダウンサンプリングのパラメータを設定してください。", textStyle);
        GUILayout.Space(10);

        string currentFileName = "無効";
        if (editor != null && editor.targetRenderer != null)
        {
            var loader = editor.targetRenderer.GetComponent<PointCloudLoader>();
            if (loader != null)
            {
                currentFileName = Path.GetFileName(loader.GetFilePath());
            }
        }

        GUILayout.Label($"対象ファイル: {currentFileName}", textStyle);
        GUILayout.Label("出力先: 入力ファイル親フォルダ内の /downsample/ フォルダ", textStyle);
        GUILayout.Space(10);

        GUILayout.BeginHorizontal();
        GUILayout.Label("ボクセルサイズ (mm):", textStyle, GUILayout.Width(150));
        downsampleVoxelSizeStr = GUILayout.TextField(downsampleVoxelSizeStr);
        GUILayout.EndHorizontal();

        GUILayout.Space(5);
        GUILayout.Label("処理モードを選択してください:", textStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("1: 全体結合のみ", downsampleMode == 1 ? activeButtonStyle : buttonStyle)) downsampleMode = 1;
        if (GUILayout.Button("2: 部位・個別のみ", downsampleMode == 2 ? activeButtonStyle : buttonStyle)) downsampleMode = 2;
        if (GUILayout.Button("3: 両方実行", downsampleMode == 3 ? activeButtonStyle : buttonStyle)) downsampleMode = 3;
        GUILayout.EndHorizontal();

        GUILayout.Space(20);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("実行", activeButtonStyle, GUILayout.Height(35)))
        {
            showDownsampleDialog = false;
            SaveSettings();
            ExecuteDownsampling();
        }
        GUILayout.Space(10);
        if (GUILayout.Button("キャンセル", buttonStyle, GUILayout.Height(35)))
        {
            showDownsampleDialog = false;
        }
        GUILayout.EndHorizontal();
    }

    private void DrawScaleCalibWindow(int windowID)
    {
        GUILayout.Space(10);
        GUILayout.Label("実寸法（mm）と計測値（unit）を指定してスケール校正を実行します。", textStyle);
        GUILayout.Space(10);

        GUILayout.BeginHorizontal();
        GUILayout.Label("実寸法 (mm):", textStyle, GUILayout.Width(150));
        scaleRealDiameterStr = GUILayout.TextField(scaleRealDiameterStr);
        GUILayout.EndHorizontal();

        GUILayout.Space(5);
        GUILayout.Label("計測値 (unit) (カンマ区切りで複数可):", textStyle);
        scaleMeasurementsStr = GUILayout.TextField(scaleMeasurementsStr);
        GUILayout.Label("例: 0.052, 0.051, 0.053", textStyle);

        if (editor != null && editor.MeasurementPointCount >= 2)
        {
            float localDist = editor.GetMeasurementLength();
            if (GUILayout.Button($"[現在の線の長さをコピー ({localDist:F5})]", buttonStyle))
            {
                scaleMeasurementsStr = localDist.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        GUILayout.Space(20);

        GUILayout.BeginHorizontal();
        bool hasValidInput = !string.IsNullOrEmpty(scaleMeasurementsStr.Trim());
        GUI.enabled = hasValidInput;
        if (GUILayout.Button("校正実行", activeButtonStyle, GUILayout.Height(35)))
        {
            showScaleCalibDialog = false;
            SaveSettings();
            ExecuteScaleCalibration();
        }
        GUI.enabled = true;
        GUILayout.Space(10);
        if (GUILayout.Button("キャンセル", buttonStyle, GUILayout.Height(35)))
        {
            showScaleCalibDialog = false;
        }
        GUILayout.EndHorizontal();
    }

    public void ExecuteScaleCalibration()
    {
        if (!float.TryParse(scaleRealDiameterStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float parsedDiameter))
        {
            UnityEngine.Debug.LogError("基準球の実寸が有効な数値ではありません。");
            return;
        }

        if (string.IsNullOrEmpty(scaleMeasurementsStr.Trim()))
        {
            UnityEngine.Debug.LogError("NeRFでの計測値が入力されていません。");
            return;
        }

        var pm = PointCloudProgressManager.Instance;
        pm.Start("スケール校正", "Pythonプロセスを準備中...");

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var token = pm.CancellationToken;
                bool success = await PythonBridge.RunScaleCalibrationAsync(
                    parsedDiameter,
                    scaleMeasurementsStr,
                    "config/scale_calibration_report.json",
                    token
                );

                if (!token.IsCancellationRequested && success)
                {
                    scaleFinishedFlag = true;
                }
            }
            catch (System.OperationCanceledException)
            {
                UnityEngine.Debug.LogWarning("[PointCloudEditorUI] スケール校正処理がユーザーによってキャンセルされました。");
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[PointCloudEditorUI] スケール校正処理エラー: {ex.Message}");
                scaleErrorMessage = ex.Message;
                scaleFailedFlag = true;
            }
        });
    }

    private void ExecuteDownsampling()
    {
        if (!float.TryParse(downsampleVoxelSizeStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float parsedVoxelSize))
        {
            UnityEngine.Debug.LogError("ボクセルサイズが有効な数値ではありません。");
            return;
        }

        if (editor == null || editor.targetRenderer == null)
        {
            UnityEngine.Debug.LogError("対象のPointCloudRendererが見つかりません。");
            return;
        }

        var loader = editor.targetRenderer.GetComponent<PointCloudLoader>();
        if (loader == null || string.IsNullOrEmpty(loader.GetFilePath()))
        {
            UnityEngine.Debug.LogError("ロードされた点群ファイルが見つかりません。");
            return;
        }

        DownsamplePaths paths = PointCloudDownsampleService.BuildPaths(loader.GetFilePath());

        var pm = PointCloudProgressManager.Instance;
        pm.Start("ダウンサンプリング", "最新のアノテーション状態を一時保存中...");

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var token = pm.CancellationToken;

                // 1. 最新のアノテーション状態を _labeled.ply としてバイナリ形式で一時保存
                UnityEngine.Debug.Log($"[Downsample Auto] Exporting latest annotations to: {paths.TemporaryLabeledPath}");
                await editor.ExportLabeledPointsAsync(paths.TemporaryLabeledPath, true, token);

                if (token.IsCancellationRequested) return;

                // 2. エクスポートされた一時ファイルを用いてPythonダウンサンプリングを起動
                pm.Update(0.1f, "Pythonプロセスを開始中...");
                bool success = await PythonBridge.RunDownsamplingAsync(
                    paths.TemporaryLabeledPath,
                    paths.OutputDirectory,
                    "config/scale_calibration_report.json",
                    downsampleMode,
                    parsedVoxelSize,
                    token
                );

                if (!token.IsCancellationRequested && success)
                {
                    downsampleFinishedFlag = true;
                }
            }
            catch (System.OperationCanceledException)
            {
                UnityEngine.Debug.LogWarning("[PointCloudEditorUI] ダウンサンプリング処理がユーザーによってキャンセルされました。");
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[PointCloudEditorUI] ダウンサンプリング処理エラー: {ex.Message}");
                downsampleErrorMessage = ex.Message;
                downsampleFailedFlag = true;
            }
        });
    }

    void OnDestroy()
    {
        if (lineTex != null)
        {
            Destroy(lineTex);
        }
        if (modalBackdropTex != null)
        {
            Destroy(modalBackdropTex);
        }
        if (progressBgTex != null)
        {
            Destroy(progressBgTex);
        }
    }
}
