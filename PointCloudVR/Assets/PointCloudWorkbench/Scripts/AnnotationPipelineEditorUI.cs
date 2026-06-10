using UnityEngine;
using System.Collections.Generic;

namespace PointCloudWorkbench
{
    /// <summary>
    /// アノテーション分類クラス 編集 UI
    /// 画面中央上部に配置し、ドラッグ＆ドロップによる順序入れ替え（ID自動更新）、
    /// キーボード削除、名前変更共有、Undo/Redo、適用ボタンをコンパクトに提供する。
    /// </summary>
    public class AnnotationPipelineEditorUI : MonoBehaviour
    {
        private PointCloudEditor editor;
        private PointCloudEditorUI editorUI;

        // プリセットデータ
        private AnnotationPresetListWrapper presetWrapper;
        private AnnotationPresetData activePreset;

        // 選択されたクラスID (初期値は0の未分類。-1や選択解除はしない)
        private int selectedClassId = 0;

        // ドラッグ＆ドロップ用状態変数
        private int draggingClassIndex = -1;
        private Vector2 dragMouseOffset;
        private bool isDragging = false;
        private Vector2 dragStartMousePos;

        // クラス追加・名前変更で共有するテキスト入力フィールド
        private string classNameInput = "新規クラス";

        // プリセット用ポップアップウィンドウ制御
        private bool isPresetPopupOpen = false;
        private bool shouldFocusPresetField = false;
        private string presetSaveName = "NewAnnotationPreset";
        private Vector2 presetScroll = Vector2.zero;
        private Rect presetPopupRect;

        // UI設定 (パラメータパネルを廃止して高さはTOP_Hのみ)
        private const float BAR_X = 490f;
        private const float RIGHT_W = 480f;
        private const float PAL_W = 200f; // 左カラムの幅を200fに広げて文字サイズ拡大に対応
        private const float TOP_H = 160f;

        private GUIStyle panelStyle, titleStyle, hintStyle, labelStyle;
        private GUIStyle blockStyle, activeBlockStyle, paletteBlockStyle, textFieldStyle;
        private bool stylesInitialized = false;

        void Start()
        {
            editor = GetComponent<PointCloudEditor>();
            editorUI = GetComponent<PointCloudEditorUI>();

            // プリセットの読み込み
            presetWrapper = AnnotationPresetManager.LoadPresets();
            if (presetWrapper.presets.Count > 0)
            {
                activePreset = presetWrapper.presets[0];
            }
            else
            {
                activePreset = AnnotationPresetManager.CreateDefaultPreset();
                presetWrapper.presets.Add(activePreset);
            }

            // 初期選択を未分類に
            selectedClassId = 0;
            if (activePreset.classes.Count > 0)
            {
                classNameInput = activePreset.classes[0].name;
            }

            ApplyPresetColorsToRenderer();
        }

        void Update()
        {
            HandleKeyboard();
        }

        private void HandleKeyboard()
        {
            if (isPresetPopupOpen) return;
            if (activePreset == null || editor == null) return;

            // テキスト入力フィールドにフォーカスがある場合はキー入力を無視する（IMEやBackspaceの競合を回避）
            if (GUIUtility.keyboardControl != 0) return;

            // Delete または Backspace で選択中のクラスを削除 (未分類=0 は削除不可)
            if (selectedClassId > 0)
            {
                if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))
                {
                    DeleteClass(selectedClassId);
                }
            }
        }

        public AnnotationPresetData GetActivePreset()
        {
            return activePreset;
        }

        public void ApplyPresetColorsToRenderer()
        {
            if (editor == null || editor.targetRenderer == null || activePreset == null) return;

            Vector4[] colors = new Vector4[64];
            for (int i = 0; i < 64; i++)
            {
                colors[i] = new Vector4(0.5f, 0.5f, 0.5f, 1.0f); // Default grey
            }

            foreach (var cls in activePreset.classes)
            {
                if (cls.id >= 0 && cls.id < 64)
                {
                    colors[cls.id] = cls.GetColor();
                }
            }

            editor.targetRenderer.SetLabelColors(colors);
        }

        private void InitStyles()
        {
            if (stylesInitialized) return;

            Texture2D Tex(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t; }

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = Tex(new Color(0.09f, 0.11f, 0.15f, 0.97f));
            panelStyle.border = new RectOffset(1, 1, 1, 1);

            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold }; // 14 -> 17
            titleStyle.normal.textColor = new Color(0.22f, 0.80f, 1f);

            hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Italic };
            hintStyle.normal.textColor = new Color(0.55f, 0.55f, 0.62f);

            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            labelStyle.normal.textColor = new Color(0.88f, 0.88f, 0.92f);

            blockStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold }; // 13 -> 15
            blockStyle.normal.textColor = Color.white;
            blockStyle.normal.background = Tex(new Color(0.24f, 0.28f, 0.36f));
            blockStyle.wordWrap = false;

            activeBlockStyle = new GUIStyle(blockStyle);
            activeBlockStyle.normal.background = Tex(new Color(0.1f, 0.55f, 0.28f)); // Green highlight

            paletteBlockStyle = new GUIStyle(blockStyle) { fontSize = 14 }; // 11 -> 14
            paletteBlockStyle.normal.background = Tex(new Color(0.17f, 0.20f, 0.27f));

            textFieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 15 }; // Added for bigger input text

            stylesInitialized = true;
        }

        // 明示的に描画座標(currentY)を受け取って描画し、高さを進める (PARAM_H は描画しない)
        public void DrawGUI(ref float currentY)
        {
            if (editor == null || activePreset == null) return;
            InitStyles();

            float barW = Screen.width - BAR_X - RIGHT_W - 30f;
            float barH = TOP_H; // パネルの高さは160px固定

            Rect bar = new Rect(BAR_X, currentY, barW, barH);
            GUI.Box(bar, "", panelStyle);

            // 1. パレット部分（新規追加・名前変更・適用・Undo/Redo）
            DrawControlPalette(bar);

            // 2. クラスブロックレーン
            DrawClassLane(bar);

            // 3. プリセットポップアップ
            DrawPresetMenu(currentY);

            // 4. ドラッグゴースト
            DrawDragGhost();

            // 描画した高さ分 currentY を進める
            currentY += barH + 10f;
        }

        private void DrawControlPalette(Rect bar)
        {
            float px = bar.x + 8f;
            float py = bar.y + 6f;
            float bW = PAL_W - 16f;
            float titleH = 24f;

            GUI.Label(new Rect(px, py, bW, titleH), "クラス編集 / 操作", titleStyle);
            py += titleH + 4f;

            // クラス名入力 (共有)
            classNameInput = GUI.TextField(new Rect(px, py, bW, 26f), classNameInput, textFieldStyle);
            py += 31f;

            // 「追加」と「変更」の横並びボタン
            float halfBtnW = (bW - 6f) / 2f;
            if (GUI.Button(new Rect(px, py, halfBtnW, 30f), "+ 追加", paletteBlockStyle))
            {
                AddClass(classNameInput);
                classNameInput = "新規クラス";
            }
            
            // 選択中のクラス名変更 (未分類=0 は変更不可)
            bool canEdit = selectedClassId > 0;
            GUI.enabled = canEdit;
            if (GUI.Button(new Rect(px + halfBtnW + 6f, py, halfBtnW, 30f), "✏ 変更", paletteBlockStyle))
            {
                RenameClass(selectedClassId, classNameInput);
            }
            GUI.enabled = true;
            py += 36f;

            // 上書き防止設定
            editor.selectOnlyUnclassified = GUI.Toggle(new Rect(px, py, bW, 20f), editor.selectOnlyUnclassified, " 未分類のみを選択対象にする (上書き防止)");
            py += 24f;

            // 選択した点にラベルを適用
            if (GUI.Button(new Rect(px, py, bW, 34f), "🏷 選択点に適用", activeBlockStyle))
            {
                editor.activeLabelClass = selectedClassId;
                editor.AssignLabelToSelected();
                editor.targetRenderer.colorMode = 2; // Auto-toggle to label color mode
            }
        }

        private void DrawClassLane(Rect bar)
        {
            const float p = 5f;
            const float titleH = 20f;
            const float presetW = 90f;
            const float redoW = 80f;
            const float undoW = 80f;

            float lx = bar.x + PAL_W + 6f;
            float btnX = bar.x + bar.width - p - presetW;
            float redoX = btnX - 6f - redoW;
            float undoX = redoX - 6f - undoW;
            float laneW = btnX + presetW - lx;

            GUI.Label(new Rect(lx, bar.y + p + 2f, undoX - lx - 4, titleH), "アノテーションクラス (D&Dで順序/番号入れ替え, Deleteで削除)", titleStyle);

            // Undo / Redo ボタンの横並び配置 (モヤ処理と同様にレーン上部に集約)
            GUI.enabled = editor.CanAnnotationUndo;
            if (GUI.Button(new Rect(undoX, bar.y + p, undoW, 28f), "元に戻す", blockStyle))
            {
                editor.AnnotationUndo();
            }
            GUI.enabled = editor.CanAnnotationRedo;
            if (GUI.Button(new Rect(redoX, bar.y + p, redoW, 28f), "やり直す", blockStyle))
            {
                editor.AnnotationRedo();
            }
            GUI.enabled = true;

            if (GUI.Button(new Rect(btnX, bar.y + p, presetW, 28f), "プリセット", blockStyle))
            {
                isPresetPopupOpen = !isPresetPopupOpen;
                if (isPresetPopupOpen)
                {
                    presetSaveName = activePreset.presetName;
                    presetPopupRect = new Rect(btnX - 210f, bar.y + p + 30f, 300f, 320f);
                    shouldFocusPresetField = true;
                }
            }

            // レーン背景
            float laneY = bar.y + p + titleH + 6f;
            float laneH = bar.y + TOP_H - laneY - p;
            Rect lane = new Rect(lx, laneY, laneW, laneH);
            GUI.Box(lane, "", GUI.skin.textField);

            // 各クラスブロックを横並びで描画
            float bx = lane.x + 5f;
            float bH = Mathf.Min(50f, lane.height - 10f);
            float sy = lane.y + (lane.height - bH) / 2f;
            float bSpacing = 12f;

            var ev = Event.current;

            for (int i = 0; i < activePreset.classes.Count; i++)
            {
                var cls = activePreset.classes[i];
                bool isActive = editor.activeLabelClass == cls.id;
                bool isSelected = selectedClassId == cls.id;

                string txt = $"{cls.name} ({cls.id})";
                if (isActive)
                {
                    txt = "★ " + txt;
                }

                // テキストに合わせてサイズを動的に計算 (CalcSizeを使用し、見切れを防ぐ)
                float bW = Mathf.Max(120f, blockStyle.CalcSize(new GUIContent(txt)).x + 24f);

                float blockX = bx;
                bx += bW + bSpacing;

                // レーン幅に収まりきらない場合は "…" 表示
                if (blockX + bW > lane.x + lane.width - 16f)
                {
                    GUI.Label(new Rect(lane.x + lane.width - 15f, sy + (bH - 18f) / 2f, 15f, 18f), "…", titleStyle);
                    break;
                }

                Rect br = new Rect(blockX, sy, bW, bH);
                Color originalBg = GUI.backgroundColor;
                GUI.backgroundColor = cls.GetColor();

                GUI.Box(br, txt, isSelected ? activeBlockStyle : blockStyle);

                GUI.backgroundColor = originalBg;

                // クリックして選択 / D&D開始 (未分類=0 のD&D移動は禁止し、インデックス0固定)
                if (ev.type == EventType.MouseDown && br.Contains(ev.mousePosition) && ev.button == 0)
                {
                    selectedClassId = cls.id;
                    classNameInput = cls.name;
                    editor.activeLabelClass = cls.id;

                    if (cls.id > 0) // 未分類はドラッグ不可
                    {
                        draggingClassIndex = i;
                        dragMouseOffset = ev.mousePosition - br.min;
                        dragStartMousePos = ev.mousePosition;
                        isDragging = false;
                        GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
                    }
                    ev.Use();
                }
            }

            if (ev.type == EventType.MouseDrag && draggingClassIndex > 0)
            {
                if (Vector2.Distance(ev.mousePosition, dragStartMousePos) > 5f)
                {
                    isDragging = true;
                }
            }

            // D&D のドロップ処理 (可変幅ブロックに対応した位置走査型に変更)
            if (ev.type == EventType.MouseUp)
            {
                if (draggingClassIndex > 0 && GUIUtility.hotControl != 0)
                {
                    GUIUtility.hotControl = 0;
                }

                if (isDragging && draggingClassIndex > 0)
                {
                    isDragging = false;
                    if (lane.Contains(ev.mousePosition) && draggingClassIndex > 0)
                    {
                        var draggedCls = activePreset.classes[draggingClassIndex];
                        string draggedTxt = $"{(editor.activeLabelClass == draggedCls.id ? "★ " : "")}{draggedCls.name} ({draggedCls.id})";
                        float draggedW = Mathf.Max(120f, blockStyle.CalcSize(new GUIContent(draggedTxt)).x + 24f);
                        float ghostCenterX = ev.mousePosition.x - dragMouseOffset.x + draggedW / 2f;

                        int targetIndex = 1; // 未分類(0)は移動不可なので1以上

                        float curX = lane.x + 5f;
                        for (int i = 1; i < activePreset.classes.Count; i++)
                        {
                            var cls = activePreset.classes[i];
                            string txt = $"{(editor.activeLabelClass == cls.id ? "★ " : "")}{cls.name} ({cls.id})";
                            float bW = Mathf.Max(120f, blockStyle.CalcSize(new GUIContent(txt)).x + 24f);

                            // ゴーストの中央位置を境にドロップ先インデックスを判定
                            if (ghostCenterX < curX + bW / 2f)
                            {
                                targetIndex = i;
                                break;
                            }
                            else
                            {
                                targetIndex = i + 1;
                            }
                            curX += bW + bSpacing;
                        }
                        // 最後尾への追加も許可するため Count までClamp
                        targetIndex = Mathf.Clamp(targetIndex, 1, activePreset.classes.Count);

                        if (targetIndex != draggingClassIndex && targetIndex != draggingClassIndex + 1)
                        {
                            var dragged = activePreset.classes[draggingClassIndex];
                            activePreset.classes.RemoveAt(draggingClassIndex);
                            
                            // 要素を一つ抜いたことでズレるインデックスを補正
                            if (targetIndex > draggingClassIndex) targetIndex--;
                            
                            activePreset.classes.Insert(targetIndex, dragged);

                            // 順序の入れ替えに合わせて、ID (1以上) を振り直す
                            ReassignClassIds();
                        }
                    }
                    ev.Use();
                }
                draggingClassIndex = -1;
            }
        }

        private void ReassignClassIds()
        {
            if (activePreset == null) return;

            // 旧ID -> 新ID のマッピング
            Dictionary<int, int> idMap = new Dictionary<int, int>();

            // インデックス0は必ずID=0 (未分類) 固定
            if (activePreset.classes.Count > 0)
            {
                activePreset.classes[0].id = 0;
            }

            for (int i = 1; i < activePreset.classes.Count; i++)
            {
                int oldId = activePreset.classes[i].id;
                int newId = i; // インデックスを新しいIDとする (1以上)
                
                if (oldId != newId)
                {
                    idMap[oldId] = newId;
                    activePreset.classes[i].id = newId;
                }
            }

            // 新しいIDに合わせて点群のラベル値を一括置換
            if (idMap.Count > 0)
            {
                RemapClassIds(idMap);
            }

            // 選択中のクラスIDも更新
            selectedClassId = editor.activeLabelClass;

            ApplyPresetColorsToRenderer();
            AnnotationPresetManager.SavePresets(presetWrapper);
        }

        private void RemapClassIds(Dictionary<int, int> idMap)
        {
            if (editor == null || editor.targetRenderer == null) return;
            PointData[] points = editor.targetRenderer.GetPointData();
            if (points == null) return;

            // 1. PointCloudRenderer内の全レイヤーラベルを更新
            var renderer = editor.targetRenderer;
            var layers = renderer.GetAnnotationLayers();
            foreach (var kvp in layers)
            {
                byte[] labels = kvp.Value;
                for (int i = 0; i < labels.Length; i++)
                {
                    int oldId = labels[i];
                    if (idMap.ContainsKey(oldId))
                    {
                        labels[i] = (byte)idMap[oldId];
                    }
                }
            }

            // 2. 現在の pointData の label（下位8ビット）も更新
            for (int i = 0; i < points.Length; i++)
            {
                int labelVal = points[i].label;
                int oldId = labelVal & 0xFF;
                if (idMap.ContainsKey(oldId))
                {
                    labelVal &= ~0xFF;
                    labelVal |= idMap[oldId];
                    points[i].label = labelVal;
                }
            }

            renderer.UpdatePointBuffer();
            editor.MarkStatsDirty();
        }

        private void DrawDragGhost()
        {
            if (!isDragging || draggingClassIndex < 0) return;
            var mp = Event.current.mousePosition;
            var cls = activePreset.classes[draggingClassIndex];
            string txt = $"{(editor.activeLabelClass == cls.id ? "★ " : "")}{cls.name} ({cls.id})";
            float bW = Mathf.Max(120f, blockStyle.CalcSize(new GUIContent(txt)).x + 24f);

            Rect gr = new Rect(mp.x - dragMouseOffset.x, mp.y - dragMouseOffset.y, bW, 40f);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            GUI.Box(gr, txt, activeBlockStyle);
            GUI.color = old;
        }

        private void DrawPresetMenu(float currentY)
        {
            if (!isPresetPopupOpen) return;

            presetPopupRect = GUI.Window(98, presetPopupRect, DrawPresetWindow, "", panelStyle);

            var ev = Event.current;
            if (ev.type == EventType.MouseDown && !presetPopupRect.Contains(ev.mousePosition))
            {
                isPresetPopupOpen = false;
                ev.Use();
            }
        }

        private void DrawPresetWindow(int windowID)
        {
            GUILayout.BeginArea(new Rect(5, 10, presetPopupRect.width - 10, presetPopupRect.height - 20));

            GUILayout.Label("プリセット保存", titleStyle);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("AnnotationPresetNameField");
            presetSaveName = GUILayout.TextField(presetSaveName, GUILayout.Width(210));
            if (shouldFocusPresetField)
            {
                GUI.FocusControl("AnnotationPresetNameField");
                shouldFocusPresetField = false;
            }
            if (GUILayout.Button("保存", activeBlockStyle, GUILayout.Width(60)))
            {
                if (!string.IsNullOrEmpty(presetSaveName))
                {
                    var existing = presetWrapper.presets.Find(p => p.presetName == presetSaveName);
                    if (existing != null)
                    {
                        existing.classes = new List<AnnotationClassData>(activePreset.classes);
                    }
                    else
                    {
                        var newPreset = new AnnotationPresetData
                        {
                            presetName = presetSaveName,
                            classes = new List<AnnotationClassData>(activePreset.classes)
                        };
                        presetWrapper.presets.Add(newPreset);
                        activePreset = newPreset;
                    }
                    AnnotationPresetManager.SavePresets(presetWrapper);
                    isPresetPopupOpen = false;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("プリセット読込", titleStyle);

            presetScroll = GUILayout.BeginScrollView(presetScroll);
            if (presetWrapper.presets.Count == 0)
            {
                GUILayout.Label("プリセットはありません", hintStyle);
            }
            else
            {
                foreach (var p in presetWrapper.presets)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(p.presetName, blockStyle, GUILayout.Width(220)))
                    {
                        activePreset = p;
                        selectedClassId = 0;
                        if (activePreset.classes.Count > 0)
                        {
                            selectedClassId = activePreset.classes[0].id;
                            classNameInput = activePreset.classes[0].name;
                        }
                        ApplyPresetColorsToRenderer();
                        isPresetPopupOpen = false;
                    }
                    if (GUILayout.Button("削", paletteBlockStyle, GUILayout.Width(40)))
                    {
                        if (p.presetName != "Default (Plant)")
                        {
                            presetWrapper.presets.Remove(p);
                            if (activePreset == p)
                            {
                                activePreset = presetWrapper.presets[0];
                                selectedClassId = 0;
                                classNameInput = activePreset.classes[0].name;
                                ApplyPresetColorsToRenderer();
                            }
                            AnnotationPresetManager.SavePresets(presetWrapper);
                            break;
                        }
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

        private void AddClass(string name)
        {
            if (activePreset == null || string.IsNullOrEmpty(name)) return;

            // 新規追加時のIDは、リストの長さ（インデックス番号）とする
            int newId = activePreset.classes.Count;
            if (newId >= 64)
            {
                Debug.LogWarning("[AnnotationPipelineEditorUI] 最大64クラスの上限に達しました。");
                return;
            }

            Color autoColor = AnnotationPresetManager.ColorPalette[newId % AnnotationPresetManager.ColorPalette.Length];
            var newClass = new AnnotationClassData { id = newId, name = name };
            newClass.SetColor(autoColor);

            activePreset.classes.Add(newClass);
            ApplyPresetColorsToRenderer();
            AnnotationPresetManager.SavePresets(presetWrapper);

            // 追加されたクラスを選択中にする
            selectedClassId = newId;
            classNameInput = name;
            editor.activeLabelClass = newId;
        }

        private void RenameClass(int classId, string newName)
        {
            if (activePreset == null || string.IsNullOrEmpty(newName) || classId == 0) return;

            var cls = activePreset.classes.Find(c => c.id == classId);
            if (cls != null)
            {
                cls.name = newName;
                AnnotationPresetManager.SavePresets(presetWrapper);
                editor.MarkStatsDirty();
            }
        }

        private void DeleteClass(int classId)
        {
            if (activePreset == null || classId == 0) return;

            var cls = activePreset.classes.Find(c => c.id == classId);
            if (cls != null)
            {
                activePreset.classes.Remove(cls);
                
                // 点群内のこのクラスIDの点を「未分類 (0)」に置き換え
                Dictionary<int, int> idMap = new Dictionary<int, int>();
                idMap[classId] = 0;
                RemapClassIds(idMap);

                // ID を詰め直す
                ReassignClassIds();

                // 削除されたため、選択クラスを「未分類 (0)」にする
                selectedClassId = 0;
                if (activePreset.classes.Count > 0)
                {
                    classNameInput = activePreset.classes[0].name;
                }
                editor.activeLabelClass = 0;

                ApplyPresetColorsToRenderer();
                AnnotationPresetManager.SavePresets(presetWrapper);
            }
        }
    }
}
