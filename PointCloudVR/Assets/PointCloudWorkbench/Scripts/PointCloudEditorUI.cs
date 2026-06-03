using UnityEngine;
using System.IO;
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
    private bool foldoutNoiseFilter = false;
    private bool foldoutLoad = true;
    private bool foldoutLOD = true;
    private bool foldoutStats = true;

    // Export Dialog Status
    private bool showExportDialog = false;
    private Rect exportDialogRect = new Rect(0, 0, 320, 160);

    private NoiseFilterUI noiseFilterUI;
    private FilterPipelineEditorUI pipelineEditorUI;

    // Lasso drawing texture
    private Texture2D lineTex;

    // Progress Modal textures
    private Texture2D modalBackdropTex;
    private Texture2D progressBgTex;

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
        RefreshFileList();
    }

    void Update()
    {
        // 定期的なファイルリストの更新
        fileCheckTimer -= Time.deltaTime;
        if (fileCheckTimer <= 0f)
        {
            RefreshFileList();
            fileCheckTimer = 2.0f;
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
        // Block mouse interactions if modal progress dialog is running
        if (PointCloudProgressManager.Instance.IsRunning) return true;

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
            editor.brushRadius = GUILayout.HorizontalSlider(editor.brushRadius, 0.02f, 2.0f);
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
            if (GUILayout.Button("鉛直円柱 (支柱)", editor.ransacType == PointCloudEditor.RansacType.Cylinder ? activeButtonStyle : buttonStyle))
            {
                editor.ransacType = PointCloudEditor.RansacType.Cylinder;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label($"適合許容誤差 (Tolerance): {editor.ransacTolerance * 100f:F1} cm", textStyle);
            editor.ransacTolerance = GUILayout.HorizontalSlider(editor.ransacTolerance, 0.002f, 0.15f);

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
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("選択クリア", buttonStyle)) editor.ClearSelection();
            if (GUILayout.Button("選択反転", buttonStyle)) editor.InvertSelection();
            if (GUILayout.Button("選択点を削除", buttonStyle)) editor.DeleteSelected();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("削除した点を復元 (ノイズ除去クリア)", buttonStyle)) editor.RestoreDeleted();
            
            GUILayout.Label("🏷 分類アノテーションラベル選択:", textStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("茎 (1)", editor.activeLabelClass == 1 ? activeButtonStyle : buttonStyle)) editor.activeLabelClass = 1;
            if (GUILayout.Button("葉 (2)", editor.activeLabelClass == 2 ? activeButtonStyle : buttonStyle)) editor.activeLabelClass = 2;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("果実 (3)", editor.activeLabelClass == 3 ? activeButtonStyle : buttonStyle)) editor.activeLabelClass = 3;
            if (GUILayout.Button("花 (4)", editor.activeLabelClass == 4 ? activeButtonStyle : buttonStyle)) editor.activeLabelClass = 4;
            if (GUILayout.Button("支柱 (5)", editor.activeLabelClass == 5 ? activeButtonStyle : buttonStyle)) editor.activeLabelClass = 5;
            GUILayout.EndHorizontal();
            
            GUILayout.Space(2);
            if (GUILayout.Button("選択した点にラベルを適用", activeButtonStyle))
            {
                editor.AssignLabelToSelected();
                editor.targetRenderer.colorMode = 2; // Auto toggle to label color mode
            }
            GUILayout.Space(8);
        }


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
                                    var cam = Object.FindFirstObjectByType<CloudCompareCameraController>();
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
            showExportDialog = true;
        }
        GUILayout.Space(5);

        GUILayout.EndScrollView();
        GUILayout.EndArea();

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
            exportDialogRect = GUI.Window(999, exportDialogRect, DrawExportDialogWindow, "💾 PLYエクスポート設定", windowStyle);
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
            editor.ExportLabeledPoints(false);
        }
        GUILayout.Space(10);
        if (GUILayout.Button("Binary (バイナリ)", buttonStyle, GUILayout.Height(35)))
        {
            showExportDialog = false;
            editor.ExportLabeledPoints(true);
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
