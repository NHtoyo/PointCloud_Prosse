using UnityEngine;
using System.Collections.Generic;

namespace PointCloudWorkbench
{
    /// <summary>
    /// ノイズ除去パイプライン エディタ UI
    /// バーを上段(パレット+レーン)と下段(パラメータ)の2段構成にし、
    /// 外部に浮かぶパネルは一切出さない設計。
    /// </summary>
    public class FilterPipelineEditorUI : MonoBehaviour
    {
        private PointCloudEditor editor;
        private PointCloudEditorUI editorUI;
        private NoiseFilterUI noiseFilterUI;

        // D&D 状態
        private string draggingBlockType = null;
        private int    draggingSourceIndex = -1;
        private Vector2 dragMouseOffset;
        private Vector2 dragStartMousePos;
        private bool   isDragging = false;

        // 選択
        private int selectedBlockIndex = -1;

        // 右クリックメニュー
        private int     contextMenuBlockIndex = -1;
        private Vector2 contextMenuPos;
        private bool    showContextMenu = false;

        // コピーバッファ
        private string copiedBlockType = null;

        // プリセット用
        private bool isPresetPopupOpen = false;
        private bool shouldFocusPresetField = false;
        private string presetSaveName = "NewPreset";
        private Vector2 presetScroll = Vector2.zero;
        private Rect presetPopupRect;
        private Rect lastPanelRect;
        private const float CenterGroupScreenY = 15f;

        // スタイル
        private GUIStyle panelStyle, titleStyle, hintStyle, labelStyle;
        private GUIStyle blockStyle, activeBlockStyle, paletteBlockStyle;
        private bool stylesInitialized = false;

        // 左パネルと右パネルの間に配置する（画面幅に応じて動的計算）
        private float BAR_X  => PointCloudUILayout.Calculate(Screen.width, Screen.height).CenterPanel.x;
        private const float BAR_Y      = 15f;
        private const float PAL_W      = 175f;
        private const float PARAM_H    = 140f;     // 下段高さ(パラメータ) (90->140へ拡大)

        private static readonly string[] AvailableTypes =
            { "white_haze", "cc_noise", "sor", "ror", "density", "dbscan" };

        private static readonly Dictionary<string, string> DispNames = new Dictionary<string, string>
        {
            { "white_haze", "白モヤ除去"      },
            { "cc_noise",   "平面推定 (CC)"   },
            { "sor",        "統計 (SOR)"      },
            { "ror",        "半径 (ROR)"      },
            { "density",    "低密度ノイズ"    },
            { "dbscan",     "DBSCAN"          }
        };

        // =========================================================
        void Start()
        {
            editor        = GetComponent<PointCloudEditor>();
            editorUI      = GetComponent<PointCloudEditorUI>();
            noiseFilterUI = GetComponent<NoiseFilterUI>();
        }

        void Update()
        {
            if (HardwareCompatibilityDiagnostic.HasBlockingGraphicsFailure) return;
            HandleKeyboard();
        }

        // =========================================================
        // キーボードショートカット
        // =========================================================
        private void HandleKeyboard()
        {
            if (editorUI == null || !editorUI.showNoiseFilterUI) return;
            if (isPresetPopupOpen) return;
            if (noiseFilterUI?.Params?.customPipeline == null) return;
            var pl = noiseFilterUI.Params.customPipeline;

            // どのUIテキスト欄への入力も、パイプライン操作ショートカットとして扱わない。
            if (GUIUtility.keyboardControl != 0) return;

            if (selectedBlockIndex >= 0 && selectedBlockIndex < pl.Count)
            {
                if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))
                {
                    pl.RemoveAt(selectedBlockIndex);
                    selectedBlockIndex = -1;
                    return;
                }
                if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.C))
                { copiedBlockType = pl[selectedBlockIndex].name; return; }
                if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.X))
                { copiedBlockType = pl[selectedBlockIndex].name; pl.RemoveAt(selectedBlockIndex); selectedBlockIndex = -1; return; }
            }
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.V)
                && !string.IsNullOrEmpty(copiedBlockType))
            { pl.Add(MakeStep(copiedBlockType)); selectedBlockIndex = pl.Count - 1; }
        }

        // =========================================================
        // スタイル初期化
        // =========================================================
        private void InitStyles()
        {
            if (stylesInitialized) return;

            Texture2D Tex(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t; }

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = Tex(new Color(0.09f, 0.11f, 0.15f, 0.98f));
            panelStyle.border = new RectOffset(1, 1, 1, 1);

            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = new Color(0.22f, 0.80f, 1f);

            hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Italic };
            hintStyle.normal.textColor = new Color(0.55f, 0.55f, 0.62f);

            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            labelStyle.normal.textColor = new Color(0.88f, 0.88f, 0.92f);

            blockStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            blockStyle.normal.textColor  = Color.white;
            blockStyle.normal.background = Tex(new Color(0.24f, 0.28f, 0.36f));
            blockStyle.wordWrap = false;

            activeBlockStyle = new GUIStyle(blockStyle);
            activeBlockStyle.normal.background = Tex(new Color(0.12f, 0.55f, 0.88f));

            paletteBlockStyle = new GUIStyle(blockStyle) { fontSize = 12 };
            paletteBlockStyle.normal.background = Tex(new Color(0.17f, 0.20f, 0.27f));

            stylesInitialized = true;
        }

        // =========================================================
        // DrawGUI エントリ（OnGUI から明示的に呼び出される）
        // =========================================================
        public void DrawGUI(ref float currentY)
        {
            if (editor == null || noiseFilterUI == null) return;
            InitStyles();

            float barW = PointCloudUILayout.Calculate(Screen.width, Screen.height).CenterPanel.width;
            var pl = noiseFilterUI?.Params?.customPipeline;
            bool hasSelection = selectedBlockIndex >= 0 && pl != null && selectedBlockIndex < pl.Count;
            bool compact = barW < 780f;
            float paletteWidth = compact ? 122f : PAL_W;
            float laneWidth = Mathf.Max(120f, barW - paletteWidth - 22f);
            float blockWidth = compact ? 108f : 130f;
            float blockSpacing = compact ? 10f : 22f;
            float blockHeight = compact ? 40f : 50f;
            int columns = Mathf.Max(1, Mathf.FloorToInt((laneWidth - 10f + blockSpacing) / (blockWidth + blockSpacing)));
            int rows = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, pl?.Count ?? 0) / (float)columns));
            float headerHeight = compact ? 100f : 40f;
            float contentHeight = rows * (blockHeight + 12f) + 14f;
            float topHeight = Mathf.Max(160f, headerHeight + contentHeight + 14f);
            float barH = topHeight + (hasSelection ? PARAM_H : 0f);
            Rect bar = new Rect(BAR_X, currentY, barW, barH);
            lastPanelRect = new Rect(bar.x, bar.y + CenterGroupScreenY, bar.width, bar.height);

            // バー背景
            GUI.Box(bar, "", panelStyle);

            // 上段: パレット + レーン
            DrawPalette(bar, topHeight, paletteWidth, compact);
            DrawLane(bar, topHeight, paletteWidth, compact, columns, blockWidth, blockSpacing, blockHeight);

            if (hasSelection)
            {
                // 区切り線
                DrawDivider(new Rect(BAR_X + 5, currentY + topHeight, barW - 10, 1));

                // 下段: パラメータ（バー内に統合、選択時のみ描画）
                DrawParamPanel(new Rect(BAR_X, currentY + topHeight + 1, barW, PARAM_H - 1));
            }

            // ドラッグゴースト / コンテキストメニュー
            DrawDragGhost();
            DrawContextMenu();
            DrawPresetMenu();

            // このUIバーの外部をクリックした際にブロック選択を解除する
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && !bar.Contains(ev.mousePosition) && !isPresetPopupOpen)
            {
                if (!IsMouseBlockedByContextMenu())
                {
                    selectedBlockIndex = -1;
                }
            }

            // 描画した高さ分 currentY を進める (マージン 10f 追加)
            currentY += barH + 10f;
        }

        public bool IsMouseOverUI()
        {
            Vector3 mouse = Input.mousePosition;
            mouse.y = Screen.height - mouse.y;
            if (lastPanelRect.Contains(mouse)) return true;
            if (isPresetPopupOpen && new Rect(presetPopupRect.x, presetPopupRect.y + CenterGroupScreenY,
                presetPopupRect.width, presetPopupRect.height).Contains(mouse)) return true;
            return showContextMenu && new Rect(contextMenuPos.x, contextMenuPos.y + CenterGroupScreenY, 88f, 72f).Contains(mouse);
        }

        private void DrawPresetMenu()
        {
            if (!isPresetPopupOpen) return;

            // GUI.Window を使用することで、クリックの背後へのすり抜け(Click-through)を防止し、
            // テキストフィールドのフォーカス入力を確実に行えるようにします。
            presetPopupRect = GUI.Window(99, presetPopupRect, DrawPresetWindow, "", panelStyle);

            var ev = Event.current;
            if (ev.type == EventType.MouseDown && !presetPopupRect.Contains(ev.mousePosition))
            {
                isPresetPopupOpen = false;
                ev.Use();
            }
        }

        private void DrawPresetWindow(int windowID)
        {
            // GUI.Windowの内部座標 (x=0, y=0 起点)
            GUILayout.BeginArea(new Rect(5, 10, presetPopupRect.width - 10, presetPopupRect.height - 20));

            GUILayout.Label("プリセット保存", titleStyle);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("PresetNameField");
            presetSaveName = GUILayout.TextField(presetSaveName, GUILayout.Width(210));
            if (shouldFocusPresetField)
            {
                GUI.FocusControl("PresetNameField");
                shouldFocusPresetField = false;
            }
            if (GUILayout.Button("保存", activeBlockStyle, GUILayout.Width(60)))
            {
                EnsurePipeline();
                NoiseFilterPresetManager.SavePreset(presetSaveName, noiseFilterUI.Params);
                isPresetPopupOpen = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("プリセット読込", titleStyle);

            presetScroll = GUILayout.BeginScrollView(presetScroll);
            var presets = NoiseFilterPresetManager.GetPresetNames();
            if (presets.Count == 0)
            {
                GUILayout.Label("プリセットはありません", hintStyle);
            }
            else
            {
                foreach (var p in presets)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(p, blockStyle, GUILayout.Width(220)))
                    {
                        NoiseFilterPresetManager.LoadPreset(p, noiseFilterUI.Params);
                        selectedBlockIndex = -1;
                        isPresetPopupOpen = false;
                    }
                    if (GUILayout.Button("削", paletteBlockStyle, GUILayout.Width(40)))
                    {
                        NoiseFilterPresetManager.DeletePreset(p);
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();

            if (GUILayout.Button("閉じる", paletteBlockStyle))
            {
                isPresetPopupOpen = false;
            }
            GUILayout.EndArea();
        }

        // =========================================================
        // パレット描画（絶対座標）
        // =========================================================
        private void DrawPalette(Rect bar, float topHeight, float paletteWidth, bool compact)
        {
            if (IsMouseBlockedByContextMenu()) return;
            float px   = bar.x + 5f;
            float py   = bar.y + 4f;
            float bW   = paletteWidth - 8f;
            float titleH = 22f; // タイトル高さを文字拡大に合わせて少し広げる
            // ボタン高さ: 上段高さから title と余白を引いてボタン数で割る
            float usable = topHeight - titleH - 8f - (AvailableTypes.Length - 1) * 3f;
            float bH = Mathf.Floor(usable / AvailableTypes.Length);
            bH = Mathf.Clamp(bH, 18f, 32f);

            GUI.Label(new Rect(px, py, bW, titleH), "パレット", titleStyle);
            py += titleH + 2f;

            for (int i = 0; i < AvailableTypes.Length; i++)
            {
                string type = AvailableTypes[i];
                string lbl  = compact ? DCompact(type) : D(type);
                Rect r = new Rect(px, py + i * (bH + 2f), bW, bH);

                if (GUI.Button(r, lbl, paletteBlockStyle))
                {
                    EnsurePipeline();
                    noiseFilterUI.Params.customPipeline.Add(MakeStep(type));
                    selectedBlockIndex = noiseFilterUI.Params.customPipeline.Count - 1;
                }

                var ev = Event.current;
                if (GUI.enabled && ev.type == EventType.MouseDown && r.Contains(ev.mousePosition) && ev.button == 0)
                {
                    draggingBlockType  = type;
                    draggingSourceIndex = -1;
                    dragMouseOffset    = ev.mousePosition - r.min;
                    ev.Use();
                }
            }
        }

        // =========================================================
        // レーン描画
        // =========================================================
        private void DrawLane(Rect bar, float topHeight, float paletteWidth, bool compact, int columns,
            float blockWidth, float blockSpacing, float blockHeight)
        {
            if (IsMouseBlockedByContextMenu()) return;
            bool allowInteraction = GUI.enabled;
            const float p      = 5f;
            const float titleH = 22f;
            float lx   = bar.x + paletteWidth + 6f;
            float laneRight = bar.x + bar.width - p;
            bool hasPreview = NoiseFilterManager.Instance.IsPreviewActive;
            float headerY = bar.y + p;
            float actionY;
            float laneY;

            if (compact)
            {
                GUI.Label(new Rect(lx, headerY + 2f, Mathf.Max(80f, laneRight - lx - 100f), 22f), "パイプライン", titleStyle);
                if (noiseFilterUI.Params != null && GUI.Button(new Rect(laneRight - 98f, headerY, 98f, 28f),
                    $"処理: {noiseFilterUI.Params.processMode}", blockStyle))
                {
                    noiseFilterUI.Params.processMode = noiseFilterUI.Params.processMode == "full" ? "downsample" : "full";
                }
                actionY = headerY + 32f;
                float gap = 4f;
                float buttonW = Mathf.Max(48f, (laneRight - lx - gap * 3f) / 4f);
                DrawHistoryButton(new Rect(lx, actionY, buttonW, 25f), "元に戻す", true);
                DrawHistoryButton(new Rect(lx + buttonW + gap, actionY, buttonW, 25f), "やり直す", false);
                if (GUI.Button(new Rect(lx + (buttonW + gap) * 2f, actionY, buttonW, 25f), "標準", blockStyle))
                    ResetToDefaultPipeline();
                if (GUI.Button(new Rect(lx + (buttonW + gap) * 3f, actionY, buttonW, 25f), "プリセット", blockStyle))
                {
                    OpenPresetPopup(lx + (buttonW + gap) * 3f, actionY);
                }
                actionY += 29f;
                float runW = Mathf.Min(90f, (laneRight - lx - (hasPreview ? gap : 0f)) / (hasPreview ? 2f : 1f));
                if (GUI.Button(new Rect(lx, actionY, runW, 25f), "▶ 実行", activeBlockStyle))
                    noiseFilterUI.RunNoiseFilterAnalysis();
                if (hasPreview && GUI.Button(new Rect(lx + runW + gap, actionY, runW, 25f), "確定", activeBlockStyle))
                {
                    CommitPreview();
                }
                laneY = actionY + 31f;
            }
            else
            {
                const float buttonHeight = 28f;
                const float gap = 6f;
                float cursor = laneRight;
                float runW = 85f;
                cursor -= runW;
                if (GUI.Button(new Rect(cursor, headerY, runW, buttonHeight), "▶ 実行", activeBlockStyle))
                    noiseFilterUI.RunNoiseFilterAnalysis();
                if (hasPreview)
                {
                    cursor -= gap + 82f;
                    if (GUI.Button(new Rect(cursor, headerY, 82f, buttonHeight), "確定", activeBlockStyle)) CommitPreview();
                }
                cursor -= gap + 85f;
                if (GUI.Button(new Rect(cursor, headerY, 85f, buttonHeight), "プリセット", blockStyle)) OpenPresetPopup(cursor, headerY);
                cursor -= gap + 95f;
                if (GUI.Button(new Rect(cursor, headerY, 95f, buttonHeight), "標準構成", blockStyle)) ResetToDefaultPipeline();
                cursor -= gap + 74f;
                DrawHistoryButton(new Rect(cursor, headerY, 74f, buttonHeight), "やり直す", false);
                cursor -= gap + 74f;
                DrawHistoryButton(new Rect(cursor, headerY, 74f, buttonHeight), "元に戻す", true);
                cursor -= gap + 112f;
                if (noiseFilterUI.Params != null && GUI.Button(new Rect(cursor, headerY, 112f, buttonHeight),
                    $"処理: {noiseFilterUI.Params.processMode}", blockStyle))
                {
                    noiseFilterUI.Params.processMode = noiseFilterUI.Params.processMode == "full" ? "downsample" : "full";
                }
                GUI.Label(new Rect(lx, headerY + 2f, Mathf.Max(80f, cursor - lx - 8f), titleH), "パイプライン・レーン", titleStyle);
                laneY = headerY + titleH + 6f;
            }

            // レーン背景
            float laneH = bar.y + topHeight - laneY - p;
            float laneW = laneRight - lx;
            Rect lane = new Rect(lx, laneY, laneW, laneH);
            GUI.Box(lane, "", GUI.skin.textField);

            EnsurePipeline();
            var pl = noiseFilterUI.Params.customPipeline;

            float sx = lane.x + 5f;
            float bW = blockWidth;
            float bH = Mathf.Min(blockHeight, lane.height - 10f);
            float rowStep = blockHeight + 12f;
            float sy = lane.y + 5f;

            bool clickedBlock = false;
            var  ev = Event.current;

            for (int i = 0; i < pl.Count; i++)
            {
                int row = i / columns;
                int column = i % columns;
                float bx = sx + column * (bW + blockSpacing);
                float by = sy + row * rowStep;

                var step = pl[i];
                Rect br   = new Rect(bx, by, bW, bH);
                string txt = D(step.name) + (step.enabled ? "" : "\n(無効)");
                bool selected = selectedBlockIndex == i;
                GUI.Box(br, txt, selected ? activeBlockStyle : blockStyle);

                if (i < pl.Count - 1)
                {
                    bool rowEnd = column == columns - 1;
                    Rect arrow = rowEnd
                        ? new Rect(lane.x + lane.width - 17f, by + (bH - 18f) / 2f, 14f, 18f)
                        : new Rect(bx + bW + 1f, by + (bH - 18f) / 2f, Mathf.Max(8f, blockSpacing - 2f), 18f);
                    GUI.Label(arrow, rowEnd ? "↓" : "▶", titleStyle);
                }

                // クリック / 右クリック / D&D 開始
                if (allowInteraction && ev.type == EventType.MouseDown && br.Contains(ev.mousePosition))
                {
                    clickedBlock = true;
                    if (ev.button == 0)
                    {
                        selectedBlockIndex  = i;
                        draggingBlockType   = step.name;
                        draggingSourceIndex = i;
                        dragMouseOffset     = ev.mousePosition - br.min;
                        dragStartMousePos   = ev.mousePosition;
                        isDragging          = false;
                        GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
                        ev.Use();
                    }
                    else if (ev.button == 1)
                    {
                        contextMenuBlockIndex = i;
                        contextMenuPos        = ev.mousePosition;
                        showContextMenu       = true;
                        ev.Use();
                    }
                }
            }

            // ブロック以外のレーン内クリック → 選択解除
            if (allowInteraction && !clickedBlock && ev.type == EventType.MouseDown && lane.Contains(ev.mousePosition))
            {
                selectedBlockIndex = -1;
                ev.Use();
            }

            // ドラッグ開始判定
            if (allowInteraction && ev.type == EventType.MouseDrag && draggingBlockType != null)
            {
                if (Vector2.Distance(ev.mousePosition, dragStartMousePos) > 5f)
                {
                    isDragging = true;
                }
            }

            // ドロップ処理
            if (allowInteraction && ev.type == EventType.MouseUp)
            {
                if (draggingBlockType != null && GUIUtility.hotControl != 0)
                {
                    GUIUtility.hotControl = 0;
                }

                if (isDragging && draggingBlockType != null)
                {
                    if (lane.Contains(ev.mousePosition))
                    {
                        // マウス位置ではなく、ゴーストUIの中央座標を基準にする
                        float ghostCenterX = ev.mousePosition.x - dragMouseOffset.x + (bW / 2f);
                        float ghostCenterY = ev.mousePosition.y - dragMouseOffset.y + (bH / 2f);
                        float relativeX = ghostCenterX - sx;
                        float relativeY = ghostCenterY - sy;
                        int targetColumn = Mathf.Clamp(Mathf.RoundToInt(relativeX / (bW + blockSpacing)), 0, columns - 1);
                        int targetRow = Mathf.Max(0, Mathf.RoundToInt(relativeY / rowStep));
                        int ins = Mathf.Clamp(targetRow * columns + targetColumn, 0, pl.Count);
                        float cellX = relativeX - targetColumn * (bW + blockSpacing);
                        if (cellX > bW * 0.55f) ins = Mathf.Min(pl.Count, ins + 1);

                        if (draggingSourceIndex >= 0)
                        {
                            var tmp = pl[draggingSourceIndex];
                            pl.RemoveAt(draggingSourceIndex);
                            if (ins > draggingSourceIndex) ins--;
                            ins = Mathf.Clamp(ins, 0, pl.Count);
                            pl.Insert(ins, tmp);
                            selectedBlockIndex = ins;
                        }
                        else
                        {
                            var ns = MakeStep(draggingBlockType);
                            pl.Insert(ins, ns);
                            selectedBlockIndex = ins;
                        }
                    }
                    else if (draggingSourceIndex >= 0)
                    {
                        pl.RemoveAt(draggingSourceIndex);
                        if (selectedBlockIndex == draggingSourceIndex) selectedBlockIndex = -1;
                    }
                    ev.Use();
                }

                draggingBlockType   = null;
                draggingSourceIndex = -1;
                isDragging = false;
            }
        }

        private void DrawHistoryButton(Rect rect, string label, bool undo)
        {
            bool wasEnabled = GUI.enabled;
            bool canRun = undo ? editor.CanAnnotationUndo : editor.CanAnnotationRedo;
            GUI.enabled = wasEnabled && canRun;
            if (GUI.Button(rect, label, blockStyle))
            {
                bool applied = undo ? editor.AnnotationUndo() : editor.AnnotationRedo();
                if (applied) editor.MarkStatsDirty();
                else PointCloudProgressManager.Instance.ShowError("点群編集履歴", "直前の点群編集を適用できませんでした。状態を確認して再試行してください。");
            }
            GUI.enabled = wasEnabled;
        }

        private void OpenPresetPopup(float x, float y)
        {
            isPresetPopupOpen = !isPresetPopupOpen;
            if (!isPresetPopupOpen) return;

            presetSaveName = "NewPreset";
            float popupWidth = Mathf.Min(300f, Screen.width - 10f);
            float popupHeight = Mathf.Min(320f, Screen.height - 10f);
            presetPopupRect = new Rect(
                Mathf.Clamp(x - 100f, 5f, Screen.width - popupWidth - 5f),
                Mathf.Clamp(y + 30f, 5f, Screen.height - popupHeight - 5f),
                popupWidth,
                popupHeight);
            shouldFocusPresetField = true;
        }

        private void CommitPreview()
        {
            if (NoiseFilterManager.Instance.CommitRemoval(editor.targetRenderer))
            {
                editor.MarkStatsDirty();
                return;
            }

            string detail = NoiseFilterManager.Instance.LastMutationFailure;
            PointCloudProgressManager.Instance.ShowError("ノイズ確定",
                string.IsNullOrWhiteSpace(detail) ? "点群ラベルを更新できませんでした。" : detail);
        }

        // =========================================================
        // 区切り線
        // =========================================================
        private void DrawDivider(Rect r)
        {
            var old = GUI.color;
            GUI.color = new Color(0.28f, 0.38f, 0.50f, 0.85f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        // =========================================================
        // パラメータパネル（バー下段、外部には一切出ない）
        // =========================================================
        private void DrawParamPanel(Rect r)
        {
            if (IsMouseBlockedByContextMenu()) return;
            var pl = noiseFilterUI?.Params?.customPipeline;

            if (pl == null || selectedBlockIndex < 0 || selectedBlockIndex >= pl.Count)
            {
                // 選択なし: ヒントのみ表示
                GUI.Label(
                    new Rect(r.x + 12, r.y + (r.height - 16) / 2f, r.width - 20, 16f),
                    "↑ レーン内のブロックをクリックするとパラメータを編集できます",
                    hintStyle);
                return;
            }

            var step = pl[selectedBlockIndex];
            bool guiEnabledBeforeParameters = GUI.enabled;
            bool compactParameters = r.width < 560f;
            int previousLabelFontSize = labelStyle.fontSize;
            labelStyle.fontSize = compactParameters ? 11 : 13;
            float x  = r.x + 8f;
            float y  = r.y + 4f;
            float lh = 21f;

            GUI.Label(new Rect(x, y, compactParameters ? r.width - 20f : 175f, 22f), D(step.name), titleStyle);
            if (compactParameters)
            {
                y += 22f;
                step.enabled = GUI.Toggle(new Rect(x, y, 100f, 20f), step.enabled, "有効");
                y += 20f;
                step.excludeFromNext = GUI.Toggle(new Rect(x, y, r.width - 20f, 20f), step.excludeFromNext, "次段から除外");
                y += 25f;
            }
            else
            {
                step.enabled = GUI.Toggle(new Rect(x + 190f, y + 2f, 85f, 20f), step.enabled, " 有効");
                step.excludeFromNext = GUI.Toggle(new Rect(x + 285f, y + 2f, 230f, 20f), step.excludeFromNext, " 次段から除外 (exclude)");
                y += lh + 4f;
            }

            // --- 行2〜: スライダー (左右2カラム) ---
            float colW = (r.width - 20f) / 2f;
            float lw   = compactParameters ? 125f : 175f;
            float sw   = Mathf.Max(colW - lw - 15f, compactParameters ? 38f : 30f);

            GUI.enabled = guiEnabledBeforeParameters && step.enabled;

            if (step is WhiteHazeConfig wh)
            {
                wh.brightness = Slider   (x,        y, lw, sw, $"最小輝度 ≥: {wh.brightness:F0}", wh.brightness, 100f, 255f);
                wh.saturation = Slider   (x + colW, y, lw, sw, $"最大彩度 ≤: {wh.saturation:F2}", wh.saturation, 0.01f, 1f);
            }
            else if (step is SorConfig sor)
            {
                sor.nb  = SliderInt(x,        y, lw, sw, $"近傍点数: {sor.nb}",        sor.nb,  5, 50);
                sor.std = Slider   (x + colW, y, lw, sw, $"StdMul: {sor.std:F2}",      sor.std, 0.5f, 3f);
            }
            else if (step is RorConfig ror)
            {
                ror.mul = Slider   (x,        y, lw, sw, $"半径倍率: {ror.mul:F2}",    ror.mul, 1f, 10f);
                ror.min = SliderInt(x + colW, y, lw, sw, $"最小近傍: {ror.min}",       ror.min, 1, 30);
            }
            else if (step is DensityConfig dn)
            {
                dn.k          = SliderInt(x,        y, lw, sw, $"近傍点数 k: {dn.k}",              dn.k,          3, 32);
                dn.percentile = Slider   (x + colW, y, lw, sw, $"候補率(下位%): {dn.percentile:F1}", dn.percentile, 0f, 20f);
            }
            else if (step is DbscanConfig db)
            {
                db.eps     = Slider   (x,        y, lw, sw, $"Eps倍率: {db.eps:F2}",       db.eps,     1f, 10f);
                db.min     = SliderInt(x + colW, y, lw, sw, $"MinPoints: {db.min}",         db.min,     2, 50);
                y += lh + 2f;
                db.cluster = SliderInt(x,        y, lw, sw, $"最小クラスタ: {db.cluster}", db.cluster, 10, 1000);
            }
            else if (step is CcConfig cc)
            {
                // 1行目: KNN/Radius トグル + 値スライダー
                cc.useKnn = GUI.Toggle(new Rect(x,        y, 115f, 20f), cc.useKnn, " KNN");
                cc.useKnn = !GUI.Toggle(new Rect(x + 120f, y, 125f, 20f), !cc.useKnn, " Radius");

                if (cc.useKnn)
                    cc.k      = SliderInt(x + colW, y, lw, sw, $"k: {cc.k}",                   cc.k,      3, 50);
                else
                    cc.radius = Slider   (x + colW, y, lw, sw, $"半径: {cc.radius:F1} mm",      cc.radius, 5f, 200f);

                // 2行目: 相対/絶対 トグル + 値スライダー
                y += lh + 2f;
                cc.useRelative = GUI.Toggle(new Rect(x,         y, 115f, 20f), cc.useRelative, " 相対σ");
                cc.useRelative = !GUI.Toggle(new Rect(x + 120f, y, 125f, 20f), !cc.useRelative, " 絶対誤差");

                if (cc.useRelative)
                    cc.sigma = Slider(x + colW, y, lw, sw, $"Sigma: {cc.sigma:F2}", cc.sigma, 0.1f, 3f);
                else
                    cc.error = Slider(x + colW, y, lw, sw, $"絶対誤差: {cc.error:F1} mm", cc.error, 0.1f, 50f);

                // 3行目: 孤立点トグル
                y += lh + 2f;
                cc.removeIsolated = GUI.Toggle(new Rect(x, y, 180f, 20f), cc.removeIsolated, " 孤立点も除去");
            }

            GUI.enabled = guiEnabledBeforeParameters;
            labelStyle.fontSize = previousLabelFontSize;
        }

        // =========================================================
        // スライダーヘルパー
        // =========================================================
        private float Slider(float x, float y, float lw, float sw, string lbl, float val, float mn, float mx)
        {
            GUI.Label(new Rect(x, y, lw, 24f), lbl, labelStyle);
            return GUI.HorizontalSlider(new Rect(x + lw + 2f, y + 6f, sw, 16f), val, mn, mx);
        }

        private int SliderInt(float x, float y, float lw, float sw, string lbl, int val, int mn, int mx)
        {
            GUI.Label(new Rect(x, y, lw, 24f), lbl, labelStyle);
            return Mathf.RoundToInt(GUI.HorizontalSlider(new Rect(x + lw + 2f, y + 6f, sw, 16f), val, mn, mx));
        }

        // =========================================================
        // ドラッグゴースト
        // =========================================================
        private void DrawDragGhost()
        {
            if (!isDragging || draggingBlockType == null) return;
            var mp = Event.current.mousePosition;
            Rect gr = new Rect(mp.x - dragMouseOffset.x, mp.y - dragMouseOffset.y, 130f, 50f); // 拡大したブロックに大きさを合わせる (100x30 -> 130x50)
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            GUI.Box(gr, D(draggingBlockType), activeBlockStyle);
            GUI.color = old;
        }

        // =========================================================
        // 右クリックコンテキストメニュー
        // =========================================================
        private void DrawContextMenu()
        {
            if (!showContextMenu) return;
            var pl = noiseFilterUI.Params.customPipeline;
            Rect mr = new Rect(contextMenuPos.x, contextMenuPos.y, 88f, 72f);
            GUI.Box(mr, "", panelStyle);

            if (GUI.Button(new Rect(mr.x + 4, mr.y + 4,  80f, 19f), "コピー"))
            {
                if (contextMenuBlockIndex >= 0 && contextMenuBlockIndex < pl.Count)
                    copiedBlockType = pl[contextMenuBlockIndex].name;
                showContextMenu = false;
            }
            if (GUI.Button(new Rect(mr.x + 4, mr.y + 26, 80f, 19f), "削除"))
            {
                if (contextMenuBlockIndex >= 0 && contextMenuBlockIndex < pl.Count)
                {
                    pl.RemoveAt(contextMenuBlockIndex);
                    if (selectedBlockIndex == contextMenuBlockIndex) selectedBlockIndex = -1;
                }
                showContextMenu = false;
            }
            if (GUI.Button(new Rect(mr.x + 4, mr.y + 48, 80f, 19f), "閉じる"))
                showContextMenu = false;

            var ev = Event.current;
            if (ev.type == EventType.MouseDown && !mr.Contains(ev.mousePosition))
            {
                showContextMenu = false;
                ev.Use();
            }
        }

        // =========================================================
        // ユーティリティ
        // =========================================================
        private bool IsMouseBlockedByContextMenu()
        {
            if (!showContextMenu) return false;
            var ev = Event.current;
            if (ev == null) return false;

            if (ev.type == EventType.MouseDown ||
                ev.type == EventType.MouseUp ||
                ev.type == EventType.MouseDrag ||
                ev.type == EventType.ScrollWheel)
            {
                Rect mr = new Rect(contextMenuPos.x, contextMenuPos.y, 88f, 72f);
                return mr.Contains(ev.mousePosition);
            }
            return false;
        }

        private void EnsurePipeline()
        {
            if (noiseFilterUI.Params.customPipeline == null)
                noiseFilterUI.Params.customPipeline = noiseFilterUI.Params.GetPipeline();
        }

        private void ResetToDefaultPipeline()
        {
            noiseFilterUI.Params.customPipeline = new List<FilterStepConfig>
            {
                noiseFilterUI.Params.whiteHaze,
                noiseFilterUI.Params.cc,
                noiseFilterUI.Params.sor,
                noiseFilterUI.Params.ror,
                noiseFilterUI.Params.density,
                noiseFilterUI.Params.dbscan
            };
            noiseFilterUI.Params.ror.enabled = true;
            noiseFilterUI.Params.density.enabled = true;
            selectedBlockIndex = -1;
        }

        private FilterStepConfig MakeStep(string t)
        {
            switch (t)
            {
                case "white_haze": return new WhiteHazeConfig();
                case "cc_noise":   return new CcConfig();
                case "sor":        return new SorConfig();
                case "ror":        return new RorConfig();
                case "density":    return new DensityConfig();
                case "dbscan":     return new DbscanConfig();
                default:           return new FilterStepConfig { name = t, enabled = true, excludeFromNext = true };
            }
        }

        private string D(string t) => DispNames.ContainsKey(t) ? DispNames[t] : t;

        private string DCompact(string t)
        {
            switch (t)
            {
                case "white_haze": return "白モヤ除去";
                case "cc_noise": return "平面推定";
                case "sor": return "統計 (SOR)";
                case "ror": return "半径 (ROR)";
                case "density": return "低密度";
                case "dbscan": return "DBSCAN";
                default: return D(t);
            }
        }
    }
}
