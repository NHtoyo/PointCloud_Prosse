using UnityEngine;

namespace PointCloudWorkbench
{
    /// <summary>
    /// 2点間距離計測専用のコンパクトパネル UI
    /// アノテーションUI・モヤ処理UIの下に連結され、上のUIが閉じると上に詰まる連鎖式UIとして実装。
    /// </summary>
    public class DistanceMeasurementUI : MonoBehaviour
    {
        private PointCloudEditor editor;
        private PointCloudEditorUI editorUI;

        private const float BAR_X = 490f;
        private const float TOP_W = 560f;
        private const float TOP_H = 205f; // 大きめの計測UI

        private GUIStyle panelStyle, titleStyle, hintStyle, labelStyle, lengthStyle;
        private GUIStyle blockStyle, activeBlockStyle, paletteBlockStyle;
        private bool stylesInitialized = false;

        void Start()
        {
            editor = GetComponent<PointCloudEditor>();
            editorUI = GetComponent<PointCloudEditorUI>();
        }

        void Update()
        {
            if (editor == null || editorUI == null) return;
            if (!editorUI.showMeasurementUI) return;

            if (editor.activeTool != PointCloudEditor.EditTool.Measure)
            {
                editor.activeTool = PointCloudEditor.EditTool.Measure;
            }
        }

        private void InitStyles()
        {
            if (stylesInitialized) return;

            Texture2D Tex(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t; }

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = Tex(new Color(0.09f, 0.11f, 0.15f, 0.97f));
            panelStyle.border = new RectOffset(1, 1, 1, 1);

            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = new Color(0.22f, 0.80f, 1f);

            hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            hintStyle.normal.textColor = new Color(0.55f, 0.55f, 0.62f);

            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            labelStyle.normal.textColor = new Color(0.88f, 0.88f, 0.92f);

            lengthStyle = new GUIStyle(GUI.skin.label) { fontSize = 21, fontStyle = FontStyle.Bold };
            lengthStyle.normal.textColor = Color.white;

            blockStyle = new GUIStyle(GUI.skin.button) { fontSize = 17, fontStyle = FontStyle.Bold };
            blockStyle.normal.textColor = Color.white;
            blockStyle.normal.background = Tex(new Color(0.24f, 0.28f, 0.36f));
            blockStyle.wordWrap = false;

            activeBlockStyle = new GUIStyle(blockStyle);
            activeBlockStyle.normal.background = Tex(new Color(0.1f, 0.55f, 0.28f)); // Green highlight

            paletteBlockStyle = new GUIStyle(blockStyle) { fontSize = 16 };
            paletteBlockStyle.normal.background = Tex(new Color(0.17f, 0.20f, 0.27f));

            stylesInitialized = true;
        }

        public void DrawGUI(ref float currentY)
        {
            if (editor == null || editorUI == null) return;
            InitStyles();

            float barW = TOP_W;
            float barH = TOP_H;

            Rect bar = new Rect(BAR_X, currentY, barW, barH);
            GUI.Box(bar, "", panelStyle);

            DrawMeasurementSection(new Rect(bar.x + 10f, bar.y + 8f, bar.width - 20f, bar.height - 16f));

            currentY += barH + 10f;
        }

        private void DrawMeasurementSection(Rect r)
        {
            float x = r.x;
            float y = r.y;
            float w = r.width;

            GUI.Label(new Rect(x, y, w, 28f), "📏 距離計測", titleStyle);
            y += 32f;

            float modeW = (w - 8f) / 3f;
            DrawModeButton(new Rect(x, y, modeW, 32f), "2点", PointCloudEditor.MeasurementMode.TwoPoint);
            DrawModeButton(new Rect(x + modeW + 4f, y, modeW, 32f), "折れ線", PointCloudEditor.MeasurementMode.Polyline);
            DrawModeButton(new Rect(x + (modeW + 4f) * 2f, y, modeW, 32f), "曲線", PointCloudEditor.MeasurementMode.SmoothCurve);
            y += 38f;

            // 計測距離の表示（unitおよびmm併記）
            float localDist = editor.GetMeasurementLength();
            string distStr = "---";
            if (editor.MeasurementPointCount >= 2)
            {
                float scaleX = editor.targetRenderer != null ? editor.targetRenderer.transform.localScale.x : 1.0f;
                float worldDistM = localDist * scaleX;
                float worldDistMm = worldDistM * 1000f;

                distStr = $"{localDist:F5} unit  ({worldDistMm:F1} mm)";
            }
            GUI.Label(new Rect(x, y, w, 30f), $"線の長さ: {distStr}", lengthStyle);
            y += 34f;

            GUI.Label(new Rect(x, y, w, 24f), $"点数: {editor.MeasurementPointCount}    中クリックで点を追加", hintStyle);
            y += 30f;

            if (editor.MeasurementPointCount > 0)
            {
                float buttonW = (w - 6f) / 2f;
                if (GUI.Button(new Rect(x, y, buttonW, 32f), "直近点を削除", paletteBlockStyle))
                {
                    editor.RemoveLastMeasurementPoint();
                }
                if (GUI.Button(new Rect(x + buttonW + 6f, y, buttonW, 32f), "リセット", paletteBlockStyle))
                {
                    editor.ResetMeasurement();
                }
            }
        }

        private void DrawModeButton(Rect rect, string label, PointCloudEditor.MeasurementMode mode)
        {
            bool active = editor.measurementMode == mode;
            if (GUI.Button(rect, label, active ? activeBlockStyle : blockStyle))
            {
                editor.SetMeasurementMode(mode);
            }
        }
    }
}
