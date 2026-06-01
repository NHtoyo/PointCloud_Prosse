using UnityEngine;
using System.Collections.Generic;
using PointCloudWorkbench;

namespace PointCloudWorkbench
{
    public class FilterPipelineEditorUI : MonoBehaviour
    {
        private PointCloudEditor editor;
        private NoiseFilterUI noiseFilterUI;

        // ドラッグ中のブロック情報
        private string draggingBlockType = null; // "white_haze", "sor", etc.
        private int draggingSourceIndex = -1;    // レーンからのドラッグの場合の元のインデックス。パレットからは -1
        private Vector2 dragMouseOffset;

        // 選択中のブロック
        private int selectedBlockIndex = -1;

        // 右クリックコンテキストメニュー用
        private int contextMenuBlockIndex = -1;
        private Vector2 contextMenuPos;
        private bool showContextMenu = false;

        // コピーバッファ（ブロックタイプ）
        private string copiedBlockType = null;

        // GUI Styles
        private GUIStyle panelStyle;
        private GUIStyle titleStyle;
        private GUIStyle blockStyle;
        private GUIStyle activeBlockStyle;
        private GUIStyle paletteBlockStyle;
        private GUIStyle paramPanelStyle;
        private bool stylesInitialized = false;

        // GUIレイアウト定義
        private readonly float paletteWidth = 160f;
        private readonly float barHeight = 180f;

        // 利用可能なブロックの種類
        private readonly string[] availableTypes = { "white_haze", "cc_noise", "sor", "ror", "density", "dbscan" };
        private readonly Dictionary<string, string> blockDisplayNames = new Dictionary<string, string>()
        {
            { "white_haze", "白モヤ除去" },
            { "cc_noise", "平面推定 (CC)" },
            { "sor", "統計 (SOR)" },
            { "ror", "半径 (ROR)" },
            { "density", "低密度ノイズ" },
            { "dbscan", "DBSCAN" }
        };

        void Start()
        {
            editor = GetComponent<PointCloudEditor>();
            noiseFilterUI = GetComponent<NoiseFilterUI>();
        }

        private void InitializeStyles()
        {
            if (stylesInitialized) return;

            Texture2D bgTex = new Texture2D(1, 1);
            bgTex.SetPixel(0, 0, new Color(0.12f, 0.14f, 0.18f, 0.95f)); // スタイリッシュなダークグレー
            bgTex.Apply();

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = bgTex;
            panelStyle.padding = new RectOffset(10, 10, 10, 10);

            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 14;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.normal.textColor = new Color(0.15f, 0.76f, 1f);

            blockStyle = new GUIStyle(GUI.skin.button);
            blockStyle.fontSize = 12;
            blockStyle.fontStyle = FontStyle.Bold;
            blockStyle.normal.textColor = Color.white;
            
            Texture2D blockBg = new Texture2D(1, 1);
            blockBg.SetPixel(0, 0, new Color(0.24f, 0.28f, 0.35f, 1f));
            blockBg.Apply();
            blockStyle.normal.background = blockBg;

            activeBlockStyle = new GUIStyle(blockStyle);
            Texture2D activeBg = new Texture2D(1, 1);
            activeBg.SetPixel(0, 0, new Color(0.15f, 0.6f, 0.9f, 1f)); // 鮮やかな青
            activeBg.Apply();
            activeBlockStyle.normal.background = activeBg;

            paletteBlockStyle = new GUIStyle(blockStyle);
            Texture2D paletteBg = new Texture2D(1, 1);
            paletteBg.SetPixel(0, 0, new Color(0.18f, 0.2f, 0.24f, 1f));
            paletteBg.Apply();
            paletteBlockStyle.normal.background = paletteBg;

            Texture2D paramBg = new Texture2D(1, 1);
            paramBg.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.11f, 0.98f));
            paramBg.Apply();
            paramPanelStyle = new GUIStyle(GUI.skin.box);
            paramPanelStyle.normal.background = paramBg;
            paramPanelStyle.padding = new RectOffset(15, 15, 10, 10);

            stylesInitialized = true;
        }

        void Update()
        {
            HandleKeyboardInput();
        }

        private void HandleKeyboardInput()
        {
            if (noiseFilterUI == null || noiseFilterUI.Params == null) return;
            var pipeline = noiseFilterUI.Params.customPipeline;

            if (selectedBlockIndex >= 0 && selectedBlockIndex < pipeline.Count)
            {
                // Delete or Backspace key to remove block
                if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))
                {
                    pipeline.RemoveAt(selectedBlockIndex);
                    selectedBlockIndex = -1;
                }
                // Copy (Ctrl+C)
                else if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.C))
                {
                    copiedBlockType = pipeline[selectedBlockIndex].name;
                }
                // Cut (Ctrl+X)
                else if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.X))
                {
                    copiedBlockType = pipeline[selectedBlockIndex].name;
                    pipeline.RemoveAt(selectedBlockIndex);
                    selectedBlockIndex = -1;
                }
            }

            // Paste (Ctrl+V)
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.V))
            {
                if (!string.IsNullOrEmpty(copiedBlockType))
                {
                    var newStep = CreateStepConfig(copiedBlockType);
                    pipeline.Add(newStep);
                    selectedBlockIndex = pipeline.Count - 1;
                }
            }
        }

        private FilterStepConfig CreateStepConfig(string typeName)
        {
            switch (typeName)
            {
                case "white_haze": return new WhiteHazeConfig();
                case "cc_noise": return new CcConfig();
                case "sor": return new SorConfig();
                case "ror": return new RorConfig();
                case "density": return new DensityConfig();
                case "dbscan": return new DbscanConfig();
                default: return new FilterStepConfig { name = typeName, enabled = true, excludeFromNext = false };
            }
        }

        void OnGUI()
        {
            if (editor == null || noiseFilterUI == null) return;
            InitializeStyles();

            float totalWidth = Screen.width - 40f;
            Rect pipelineRect = new Rect(20f, 15f, totalWidth, barHeight);

            // パイプライン領域全体の描画
            GUILayout.BeginArea(pipelineRect, panelStyle);
            GUILayout.BeginHorizontal();

            // 1. パレット部分
            DrawPalette();

            GUILayout.Space(10);
            
            // 2. パイプラインレーン
            DrawLane(pipelineRect);

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // 3. パラメータ詳細展開
            DrawParameterDetails(pipelineRect);

            // 4. ドラッグ中のゴーストブロック描画
            DrawDragGhost();

            // 5. コンテキストメニュー
            DrawContextMenu();
        }

        private void DrawPalette()
        {
            GUILayout.BeginVertical(GUILayout.Width(paletteWidth));
            GUILayout.Label("🧱 フィルタブロックパレット", titleStyle);
            GUILayout.Space(5);

            foreach (var type in availableTypes)
            {
                string displayName = blockDisplayNames.ContainsKey(type) ? blockDisplayNames[type] : type;
                Rect rect = GUILayoutUtility.GetRect(new GUIContent(displayName), paletteBlockStyle, GUILayout.Height(22));
                
                if (GUI.Button(rect, displayName, paletteBlockStyle))
                {
                    // ボタンクリックでも追加できるようにする
                    if (noiseFilterUI.Params.customPipeline == null)
                        noiseFilterUI.Params.customPipeline = new List<FilterStepConfig>();
                    noiseFilterUI.Params.customPipeline.Add(CreateStepConfig(type));
                    selectedBlockIndex = noiseFilterUI.Params.customPipeline.Count - 1;
                }

                // D&D のドラッグ開始検知
                Event evt = Event.current;
                if (evt.type == EventType.MouseDown && rect.Contains(evt.mousePosition) && evt.button == 0)
                {
                    draggingBlockType = type;
                    draggingSourceIndex = -1;
                    dragMouseOffset = evt.mousePosition - rect.min;
                    evt.Use();
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawLane(Rect totalArea)
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();
            GUILayout.Label("🔄 パイプライン実行レーン (左から右へ順次実行。D&Dで並べ替え/追加可能)", titleStyle);
            
            // モード切替と実行
            GUILayout.FlexibleSpace();
            if (noiseFilterUI.Params != null)
            {
                string modeText = noiseFilterUI.Params.processMode == "full" ? "全体適用" : "プレビュー";
                if (GUILayout.Button($"モード: {modeText}", GUILayout.Width(110)))
                {
                    noiseFilterUI.Params.processMode = noiseFilterUI.Params.processMode == "full" ? "downsample" : "full";
                }
            }
            if (GUILayout.Button("🚀 実行", activeBlockStyle, GUILayout.Width(80)))
            {
                noiseFilterUI.RunNoiseFilterAnalysis();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(5);

            Rect laneRect = GUILayoutUtility.GetRect(GUILayout.ExpandWidth(true), GUILayout.Height(110));
            GUI.Box(laneRect, "", GUI.skin.textField);

            var pipeline = noiseFilterUI.Params.customPipeline;
            if (pipeline == null || pipeline.Count == 0)
            {
                // 初期化
                pipeline = noiseFilterUI.Params.GetPipeline();
                noiseFilterUI.Params.customPipeline = pipeline;
            }

            float blockWidth = 120f;
            float blockHeight = 45f;
            float spacing = 25f; // 矢印の突き出しを考慮
            float startX = laneRect.x + 10f;
            float startY = laneRect.y + (laneRect.height - blockHeight) / 2f;

            for (int i = 0; i < pipeline.Count; i++)
            {
                var step = pipeline[i];
                float x = startX + i * (blockWidth + spacing);
                Rect bRect = new Rect(x, startY, blockWidth, blockHeight);

                string disp = blockDisplayNames.ContainsKey(step.name) ? blockDisplayNames[step.name] : step.name;
                if (!step.enabled) disp += " (無効)";

                // 選択時と非選択時でスタイル切替
                GUIStyle currentStyle = (selectedBlockIndex == i) ? activeBlockStyle : blockStyle;
                
                // ブロックの描画 (矢印の形状を表現する)
                GUI.Box(bRect, disp, currentStyle);

                // 次のブロックがある場合、矢印記号 "▶" を間隔部分に描画
                if (i < pipeline.Count - 1)
                {
                    Rect arrowRect = new Rect(x + blockWidth + 5f, startY + (blockHeight - 20f) / 2f, 15f, 20f);
                    GUI.Label(arrowRect, "▶", titleStyle);
                }

                // D&D、クリック、右クリックのイベント処理
                Event evt = Event.current;
                if (evt.type == EventType.MouseDown && bRect.Contains(evt.mousePosition))
                {
                    if (evt.button == 0) // 左クリック
                    {
                        selectedBlockIndex = i;
                        draggingBlockType = step.name;
                        draggingSourceIndex = i;
                        dragMouseOffset = evt.mousePosition - bRect.min;
                        evt.Use();
                    }
                    else if (evt.button == 1) // 右クリック
                    {
                        contextMenuBlockIndex = i;
                        contextMenuPos = evt.mousePosition + new Vector2(totalArea.x, totalArea.y);
                        showContextMenu = true;
                        evt.Use();
                    }
                }
            }

            // ドラッグ中のドロップ位置判定
            Event currentEvt = Event.current;
            if (currentEvt.type == EventType.MouseUp && draggingBlockType != null)
            {
                if (laneRect.Contains(currentEvt.mousePosition))
                {
                    // 最も近いインデックス（挿入位置）の計算
                    float localMouseX = currentEvt.mousePosition.x - startX;
                    int insertIndex = Mathf.Clamp(Mathf.RoundToInt(localMouseX / (blockWidth + spacing)), 0, pipeline.Count);

                    if (draggingSourceIndex >= 0)
                    {
                        // レーン内移動
                        var temp = pipeline[draggingSourceIndex];
                        pipeline.RemoveAt(draggingSourceIndex);
                        
                        // 削除によってインデックスが詰まるのを考慮
                        if (insertIndex > draggingSourceIndex) insertIndex--;
                        insertIndex = Mathf.Clamp(insertIndex, 0, pipeline.Count);
                        
                        pipeline.Insert(insertIndex, temp);
                        selectedBlockIndex = insertIndex;
                    }
                    else
                    {
                        // パレットからの新規追加
                        var newStep = CreateStepConfig(draggingBlockType);
                        pipeline.Insert(insertIndex, newStep);
                        selectedBlockIndex = insertIndex;
                    }
                }
                else if (draggingSourceIndex >= 0)
                {
                    // レーンの外へドロップされたら削除
                    pipeline.RemoveAt(draggingSourceIndex);
                    if (selectedBlockIndex == draggingSourceIndex) selectedBlockIndex = -1;
                }

                draggingBlockType = null;
                draggingSourceIndex = -1;
                currentEvt.Use();
            }

            GUILayout.EndVertical();
        }

        private void DrawParameterDetails(Rect totalArea)
        {
            var pipeline = noiseFilterUI.Params.customPipeline;
            if (pipeline == null || selectedBlockIndex < 0 || selectedBlockIndex >= pipeline.Count) return;

            var step = pipeline[selectedBlockIndex];
            
            float py = totalArea.y + totalArea.height + 10f;
            float ph = 260f;
            Rect detailsRect = new Rect(20f, py, totalArea.width, ph);

            GUILayout.BeginArea(detailsRect, paramPanelStyle);
            
            // ブロック名タイトルと有効化トグル
            GUILayout.BeginHorizontal();
            string displayName = blockDisplayNames.ContainsKey(step.name) ? blockDisplayNames[step.name] : step.name;
            GUILayout.Label($"⚙ パラメータ設定: {displayName}", titleStyle);
            GUILayout.FlexibleSpace();
            step.enabled = GUILayout.Toggle(step.enabled, " このステップを有効化");
            step.excludeFromNext = GUILayout.Toggle(step.excludeFromNext, " 検出された点を次段以降の計算から除外 (exclude)");
            GUILayout.EndHorizontal();
            GUILayout.Box("", GUILayout.Height(1));
            GUILayout.Space(5);

            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.normal.textColor = Color.white;

            // 個別のパラメータUI描画
            GUI.enabled = step.enabled;
            DrawFilterSpecificParams(step, labelStyle);
            GUI.enabled = true;

            GUILayout.EndArea();
        }

        private void DrawFilterSpecificParams(FilterStepConfig step, GUIStyle labelStyle)
        {
            var p = noiseFilterUI.Params;
            if (step is WhiteHazeConfig wh)
            {
                GUILayout.Label($"最小輝度 (Brightness >=): {wh.brightness:F1}", labelStyle);
                wh.brightness = GUILayout.HorizontalSlider(wh.brightness, 100.0f, 255.0f);
                GUILayout.Label($"最大彩度 (Saturation <=): {wh.saturation:F2}", labelStyle);
                wh.saturation = GUILayout.HorizontalSlider(wh.saturation, 0.01f, 1.0f);
            }
            else if (step is CcConfig cc)
            {
                GUILayout.BeginHorizontal();
                cc.useKnn = GUILayout.Toggle(cc.useKnn, "KNN (近傍点数) モード");
                cc.useKnn = !GUILayout.Toggle(!cc.useKnn, "Radius (近傍半径) モード");
                GUILayout.EndHorizontal();

                if (cc.useKnn)
                {
                    GUILayout.Label($"近傍点数 (k): {cc.k}", labelStyle);
                    cc.k = Mathf.RoundToInt(GUILayout.HorizontalSlider(cc.k, 3f, 50f));
                }
                else
                {
                    GUILayout.Label($"近傍半径 (radius): {cc.radius:F3} m", labelStyle);
                    cc.radius = GUILayout.HorizontalSlider(cc.radius, 0.005f, 0.2f);
                }

                GUILayout.BeginHorizontal();
                cc.useRelative = GUILayout.Toggle(cc.useRelative, "相対シグマ閾値");
                cc.useRelative = !GUILayout.Toggle(!cc.useRelative, "絶対誤差閾値");
                GUILayout.EndHorizontal();

                if (cc.useRelative)
                {
                    GUILayout.Label($"標準偏差倍率 (Sigma): {cc.sigma:F2}", labelStyle);
                    cc.sigma = GUILayout.HorizontalSlider(cc.sigma, 0.1f, 3.0f);
                }
                else
                {
                    GUILayout.Label($"絶対誤差閾値 (Error): {cc.error:F4} m", labelStyle);
                    cc.error = GUILayout.HorizontalSlider(cc.error, 0.0001f, 0.05f);
                }
                cc.removeIsolated = GUILayout.Toggle(cc.removeIsolated, "孤立点も除去");
            }
            else if (step is SorConfig sor)
            {
                GUILayout.Label($"隣接点数 (Neighbors): {sor.nb}", labelStyle);
                sor.nb = Mathf.RoundToInt(GUILayout.HorizontalSlider(sor.nb, 5f, 50f));
                GUILayout.Label($"標準偏差倍率 (StdMul): {sor.std:F2}", labelStyle);
                sor.std = GUILayout.HorizontalSlider(sor.std, 0.5f, 3.0f);
            }
            else if (step is RorConfig ror)
            {
                GUILayout.Label($"検索半径倍率 (RadiusMul): {ror.mul:F2}", labelStyle);
                ror.mul = GUILayout.HorizontalSlider(ror.mul, 1.0f, 10.0f);
                GUILayout.Label($"最小隣接点数 (MinNeighbors): {ror.min}", labelStyle);
                ror.min = Mathf.RoundToInt(GUILayout.HorizontalSlider(ror.min, 1f, 30f));
            }
            else if (step is DensityConfig dens)
            {
                GUILayout.Label($"近傍点数 (Density k): {dens.k}", labelStyle);
                dens.k = Mathf.RoundToInt(GUILayout.HorizontalSlider(dens.k, 3f, 32f));
                GUILayout.Label($"低密度閾値: {dens.threshold:F4}", labelStyle);
                dens.threshold = GUILayout.HorizontalSlider(dens.threshold, 0.0f, 100.0f);
            }
            else if (step is DbscanConfig db)
            {
                GUILayout.Label($"近傍半径倍率 (EpsMul): {db.eps:F2}", labelStyle);
                db.eps = GUILayout.HorizontalSlider(db.eps, 1.0f, 10.0f);
                GUILayout.Label($"最小近傍点数 (MinPoints): {db.min}", labelStyle);
                db.min = Mathf.RoundToInt(GUILayout.HorizontalSlider(db.min, 2f, 50f));
                GUILayout.Label($"最小クラスタサイズ: {db.cluster} 点", labelStyle);
                db.cluster = Mathf.RoundToInt(GUILayout.HorizontalSlider(db.cluster, 10f, 1000f));
            }
        }

        private void DrawDragGhost()
        {
            if (draggingBlockType == null) return;

            Vector2 mousePos = Event.current.mousePosition;
            Rect ghostRect = new Rect(mousePos.x - dragMouseOffset.x, mousePos.y - dragMouseOffset.y, 120f, 45f);

            string dispName = blockDisplayNames.ContainsKey(draggingBlockType) ? blockDisplayNames[draggingBlockType] : draggingBlockType;
            
            Color oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.6f); // ゴースト半透明
            GUI.Box(ghostRect, dispName, activeBlockStyle);
            GUI.color = oldColor;
        }

        private void DrawContextMenu()
        {
            if (!showContextMenu) return;

            Rect menuRect = new Rect(contextMenuPos.x, contextMenuPos.y, 100f, 75f);
            GUI.Box(menuRect, "", panelStyle);

            if (GUI.Button(new Rect(menuRect.x + 5f, menuRect.y + 5f, 90f, 20f), "コピー"))
            {
                var pipeline = noiseFilterUI.Params.customPipeline;
                if (contextMenuBlockIndex >= 0 && contextMenuBlockIndex < pipeline.Count)
                {
                    copiedBlockType = pipeline[contextMenuBlockIndex].name;
                }
                showContextMenu = false;
            }

            if (GUI.Button(new Rect(menuRect.x + 5f, menuRect.y + 28f, 90f, 20f), "削除"))
            {
                var pipeline = noiseFilterUI.Params.customPipeline;
                if (contextMenuBlockIndex >= 0 && contextMenuBlockIndex < pipeline.Count)
                {
                    pipeline.RemoveAt(contextMenuBlockIndex);
                    if (selectedBlockIndex == contextMenuBlockIndex) selectedBlockIndex = -1;
                }
                showContextMenu = false;
            }

            if (GUI.Button(new Rect(menuRect.x + 5f, menuRect.y + 50f, 90f, 20f), "閉じる"))
            {
                showContextMenu = false;
            }

            // メニューの外をクリックしたら閉じる
            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && !menuRect.Contains(evt.mousePosition))
            {
                showContextMenu = false;
                evt.Use();
            }
        }
    }
}
