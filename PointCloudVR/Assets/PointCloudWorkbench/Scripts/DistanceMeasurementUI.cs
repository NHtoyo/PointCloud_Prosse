using System.Collections.Generic;
using UnityEngine;

namespace PointCloudWorkbench
{
    /// <summary>計測オブジェクトの作成、一覧、編集を行うパネル。</summary>
    public class DistanceMeasurementUI : MonoBehaviour
    {
        private PointCloudEditor editor;
        private PointCloudEditorUI editorUI;
        private Vector2 contentScroll;
        private Vector2 listScroll;
        private Vector2 pointScroll;
        private Rect lastPanelRect;
        private string renameTargetId = "";
        private string renameText = "";
        private string resultExportStatus = "";
        private GUIStyle panelStyle;
        private GUIStyle titleStyle;
        private GUIStyle labelStyle;
        private GUIStyle hintStyle;
        private GUIStyle buttonStyle;
        private GUIStyle selectedButtonStyle;
        private GUIStyle statusStyle;
        private readonly List<Texture2D> styleTextures = new List<Texture2D>();
        private bool stylesInitialized;

        private float BarX => Mathf.Min(460f, Screen.width * 0.25f) + 30f;
        private float AvailableWidth => Screen.width - BarX - Mathf.Min(480f, Screen.width * 0.25f) - 30f;

        private void Start()
        {
            editor = GetComponent<PointCloudEditor>();
            editorUI = GetComponent<PointCloudEditorUI>();
        }

        private void OnDestroy()
        {
            for (int i = 0; i < styleTextures.Count; i++)
            {
                if (styleTextures[i] != null) Destroy(styleTextures[i]);
            }
        }

        public bool IsMouseOverPanel()
        {
            if (editorUI != null && !editorUI.showMeasurementUI) return false;
            Vector3 mouse = Input.mousePosition;
            mouse.y = Screen.height - mouse.y;
            return lastPanelRect.Contains(mouse);
        }

        private void CommitRename()
        {
            if (editor.RenameSelectedMeasurement(renameText) && editor.SelectedMeasurement != null)
            {
                renameText = editor.SelectedMeasurement.name;
            }
            GUI.FocusControl(null);
        }

        private Texture2D MakeTexture(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            styleTextures.Add(texture);
            return texture;
        }

        private void InitStyles()
        {
            if (stylesInitialized) return;

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = MakeTexture(new Color(0.08f, 0.10f, 0.14f, 0.85f));
            panelStyle.border = new RectOffset(1, 1, 1, 1);
            panelStyle.padding = new RectOffset(12, 12, 8, 8);

            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = new Color(0.22f, 0.80f, 1f);
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            labelStyle.normal.textColor = new Color(0.9f, 0.92f, 0.96f);
            hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
            hintStyle.normal.textColor = new Color(0.67f, 0.72f, 0.8f);
            statusStyle = new GUIStyle(hintStyle) { fontSize = 13 };
            statusStyle.normal.textColor = new Color(0.65f, 0.84f, 0.9f);

            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = true };
            buttonStyle.normal.textColor = Color.white;
            buttonStyle.normal.background = MakeTexture(new Color(0.2f, 0.24f, 0.31f));
            selectedButtonStyle = new GUIStyle(buttonStyle);
            selectedButtonStyle.normal.background = MakeTexture(new Color(0.08f, 0.48f, 0.28f));
            stylesInitialized = true;
        }

        public void DrawGUI(ref float currentY)
        {
            if (editor == null || editorUI == null) return;
            InitStyles();

            float barW = Mathf.Min(Mathf.Max(430f, AvailableWidth), Screen.width - 30f);
            float barX = Mathf.Clamp(BarX + (AvailableWidth - barW) * 0.5f, 15f, Screen.width - barW - 15f);
            float barH = Mathf.Min(460f, Mathf.Max(220f, Screen.height - currentY - 18f));
            lastPanelRect = new Rect(barX, currentY, barW, barH);
            GUI.Box(lastPanelRect, GUIContent.none, panelStyle);

            Rect contentRect = new Rect(lastPanelRect.x + 10f, lastPanelRect.y + 7f, lastPanelRect.width - 20f, lastPanelRect.height - 14f);
            GUILayout.BeginArea(contentRect);
            contentScroll = GUILayout.BeginScrollView(contentScroll, false, true);
            DrawContent();
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            currentY += barH + 10f;
        }

        private void DrawContent()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("距離計測", titleStyle, GUILayout.ExpandWidth(true));
            GUILayout.Label($"保存済み: {editor.MeasurementRecords.Count}", hintStyle, GUILayout.Width(115f));
            GUILayout.EndHorizontal();

            GUILayout.Label(editor.MeasurementStatus, statusStyle);
            if (editor.MeasurementStatus.StartsWith("保存に失敗"))
            {
                if (GUILayout.Button("保存を再試行", buttonStyle, GUILayout.Height(30f))) editor.RetrySaveMeasurementDocument();
            }

            if (editor.IsMeasurementFingerprintPending)
            {
                GUILayout.Label("PLYの指紋を確認しています。確認が終わると計測データを表示します。", hintStyle);
                return;
            }
            if (editor.HasMeasurementFingerprintMismatch)
            {
                GUILayout.Label("ファイル内容が前回保存時と異なります。計測線をこのPLYに引き継ぐ場合だけ確認してください。", hintStyle);
                if (GUILayout.Button("このPLYに計測データを引き継ぐ", buttonStyle, GUILayout.Height(36f)))
                {
                    editor.AcceptMeasurementFingerprintMismatch();
                }
                return;
            }
            if (!editor.IsMeasurementDocumentReady)
            {
                GUILayout.Label("点群を読み込むと、この点群に対応する計測JSONを開きます。", hintStyle);
                return;
            }

            DrawMeasurementList();
            DrawResultExportControl();
            DrawSelectedMeasurement();
            DrawCreationControls();
            DrawUndoControl();

            GUILayout.Space(5f);
            GUILayout.Label("点の追加・置換は中央クリックです。左クリックはカメラ操作のままです。", hintStyle);
            GUILayout.Label("距離と座標はmmで表示しています。", hintStyle);
        }

        private void DrawResultExportControl()
        {
            if (editor.MeasurementRecords.Count == 0) return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("計測結果CSVを書き出し", buttonStyle, GUILayout.Height(30f)))
            {
                try
                {
                    string path = editor.ExportMeasurementResultsCsv();
                    resultExportStatus = $"CSV保存: {System.IO.Path.GetFileName(path)}";
                    Debug.Log($"[Measurement] 計測結果CSVを保存しました: {path}");
                }
                catch (System.Exception ex)
                {
                    resultExportStatus = $"CSV出力に失敗: {ex.Message}";
                    Debug.LogError($"[Measurement] {resultExportStatus}");
                }
            }
            if (!string.IsNullOrEmpty(resultExportStatus))
                GUILayout.Label(resultExportStatus, hintStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawMeasurementList()
        {
            List<MeasurementRecord> records = editor.MeasurementRecords;
            GUILayout.Label("計測一覧", labelStyle);
            if (records.Count == 0)
            {
                GUILayout.Label("保存された計測はありません。", hintStyle);
                return;
            }

            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(Mathf.Min(132f, 36f * records.Count + 4f)));
            for (int i = 0; i < records.Count; i++)
            {
                MeasurementRecord record = records[i];
                if (record == null) continue;
                GUILayout.BeginHorizontal();
                bool selected = record.id == editor.SelectedMeasurementId;
                string rowText = $"{record.name}  |  {ModeName(record.mode)}  |  {GetLengthText(record)}";
                if (GUILayout.Button(rowText, selected ? selectedButtonStyle : buttonStyle, GUILayout.Height(32f), GUILayout.ExpandWidth(true)))
                {
                    editor.SelectMeasurement(record.id);
                    renameTargetId = record.id;
                    renameText = record.name;
                }
                Color old = GUI.color;
                GUI.color = record.visible ? Color.white : new Color(0.65f, 0.68f, 0.72f);
                if (GUILayout.Button(record.visible ? "隠す" : "表示", buttonStyle, GUILayout.Width(58f), GUILayout.Height(32f)))
                {
                    editor.SelectMeasurement(record.id);
                    editor.ToggleSelectedMeasurementVisibility();
                }
                GUI.color = old;
                if (GUILayout.Button("削除", buttonStyle, GUILayout.Width(58f), GUILayout.Height(32f)))
                {
                    editor.SelectMeasurement(record.id);
                    editor.DeleteSelectedMeasurement();
                    renameTargetId = "";
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void DrawSelectedMeasurement()
        {
            MeasurementRecord record = editor.SelectedMeasurement;
            if (record == null || editor.HasMeasurementDraft) return;
            if (renameTargetId != record.id)
            {
                renameTargetId = record.id;
                renameText = record.name;
            }

            GUILayout.BeginHorizontal();
            const string renameControlName = "MeasurementRenameField";
            GUI.SetNextControlName(renameControlName);
            renameText = GUILayout.TextField(renameText, GUILayout.MinWidth(120f), GUILayout.Height(30f));
            Event currentEvent = Event.current;
            bool enterPressed = currentEvent != null && currentEvent.type == EventType.KeyDown &&
                GUI.GetNameOfFocusedControl() == renameControlName &&
                (currentEvent.keyCode == KeyCode.Return || currentEvent.keyCode == KeyCode.KeypadEnter);
            if (enterPressed) currentEvent.Use();
            if (GUILayout.Button("名前", buttonStyle, GUILayout.Width(62f), GUILayout.Height(30f)) || enterPressed)
            {
                CommitRename();
            }
            bool colorClicked = GUILayout.Button(GUIContent.none, buttonStyle, GUILayout.Width(48f), GUILayout.Height(30f));
            Rect colorButtonRect = GUILayoutUtility.GetLastRect();
            Rect swatchBorder = new Rect(colorButtonRect.center.x - 11f, colorButtonRect.center.y - 11f, 22f, 22f);
            Rect swatchRect = new Rect(swatchBorder.x + 2f, swatchBorder.y + 2f, 18f, 18f);
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(swatchBorder, Texture2D.blackTexture);
                Color oldColor = GUI.color;
                GUI.color = record.color;
                GUI.DrawTexture(swatchRect, Texture2D.whiteTexture);
                GUI.color = oldColor;
            }
            if (colorClicked) editor.CycleSelectedMeasurementColor();
            if (GUILayout.Button("頂点編集", buttonStyle, GUILayout.Width(92f), GUILayout.Height(30f))) editor.BeginEditingSelectedMeasurement();
            GUILayout.EndHorizontal();
        }

        private void DrawCreationControls()
        {
            GUILayout.Space(4f);
            if (editor.HasMeasurementDraft)
            {
                DrawModeButtons(true);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{(editor.IsEditingMeasurementDraft ? "頂点編集中" : "新規計測")}  点数 {editor.MeasurementPointCount}  長さ {GetActiveLengthText()}", labelStyle, GUILayout.ExpandWidth(true));
                bool wasEnabled = GUI.enabled;
                GUI.enabled = wasEnabled && editor.MeasurementPointCount >= 2;
                if (GUILayout.Button("確定", selectedButtonStyle, GUILayout.Width(78f), GUILayout.Height(34f))) editor.FinishMeasurement();
                GUI.enabled = wasEnabled;
                if (GUILayout.Button("取消", buttonStyle, GUILayout.Width(78f), GUILayout.Height(34f))) editor.CancelMeasurementDraft();
                GUILayout.EndHorizontal();

                if (editor.MeasurementPointCount > 0)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("直近点を削除", buttonStyle, GUILayout.Height(30f))) editor.RemoveLastMeasurementPoint();
                    GUILayout.Label(editor.ReplacingMeasurementPointIndex >= 0
                        ? $"点{editor.ReplacingMeasurementPointIndex + 1}を置換中"
                        : "中央クリックで点を追加", hintStyle);
                    GUILayout.EndHorizontal();
                    DrawDraftPoints();
                }
            }
            else
            {
                DrawModeButtons(false);
                if (GUILayout.Button("＋ 新規計測", selectedButtonStyle, GUILayout.Height(36f)))
                {
                    editor.BeginNewMeasurement(editor.measurementMode);
                }
            }

            GUILayout.BeginHorizontal();
            bool undoWasEnabled = GUI.enabled;
            GUI.enabled = undoWasEnabled && !editor.HasMeasurementDraft && editor.CanMeasurementUndo;
            if (GUILayout.Button("↶ 計測を元に戻す", buttonStyle, GUILayout.Height(30f))) editor.UndoMeasurement();
            GUI.enabled = undoWasEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawModeButtons(bool editing)
        {
            GUILayout.BeginHorizontal();
            DrawModeButton("2点", PointCloudEditor.MeasurementMode.TwoPoint, editing);
            DrawModeButton("折れ線", PointCloudEditor.MeasurementMode.Polyline, editing);
            DrawModeButton("曲線", PointCloudEditor.MeasurementMode.SmoothCurve, editing);
            GUILayout.EndHorizontal();
        }

        private void DrawModeButton(string label, PointCloudEditor.MeasurementMode mode, bool editing)
        {
            bool active = editor.measurementMode == mode;
            if (GUILayout.Button(label, active ? selectedButtonStyle : buttonStyle, GUILayout.Height(32f)))
            {
                if (editing) editor.SetMeasurementMode(mode);
                else editor.measurementMode = mode;
            }
        }

        private void DrawDraftPoints()
        {
            GUILayout.Label("制御点", hintStyle);
            pointScroll = GUILayout.BeginScrollView(pointScroll, GUILayout.Height(Mathf.Min(96f, 26f * editor.MeasurementPointCount)));
            for (int i = 0; i < editor.measurementPath.Points.Count; i++)
            {
                Vector3 point = editor.targetRenderer != null
                    ? editor.targetRenderer.DataPointToMillimeters(editor.measurementPath.Points[i])
                    : editor.measurementPath.Points[i];
                GUILayout.BeginHorizontal();
                string pointText = $"点{i + 1}: {point.x:F1}, {point.y:F1}, {point.z:F1} mm";
                GUILayout.Label(pointText, hintStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("置換", buttonStyle, GUILayout.Width(58f), GUILayout.Height(25f))) editor.ArmMeasurementPointReplacement(i);
                if (GUILayout.Button("削除", buttonStyle, GUILayout.Width(58f), GUILayout.Height(25f))) editor.RemoveMeasurementPointAt(i);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void DrawUndoControl()
        {
            MeasurementRecord selected = editor.SelectedMeasurement;
            if (selected == null || editor.HasMeasurementDraft) return;
            GUILayout.Label($"選択中: {selected.name}  |  {selected.points.Count}点  |  {GetLengthText(selected)}", hintStyle);
        }

        private string GetActiveLengthText()
        {
            return FormatLength(editor.GetMeasurementLengthMm());
        }

        private string GetLengthText(MeasurementRecord record)
        {
            MeasurementResult result = editor.GetMeasurementResult(record);
            return result != null ? FormatLength(result.length_mm) : "-- mm";
        }

        private string FormatLength(float length)
        {
            return $"{length:F1} mm";
        }

        private static string ModeName(int mode)
        {
            if (mode == (int)PointCloudEditor.MeasurementMode.Polyline) return "折れ線";
            if (mode == (int)PointCloudEditor.MeasurementMode.SmoothCurve) return "曲線";
            return "2点";
        }
    }
}
