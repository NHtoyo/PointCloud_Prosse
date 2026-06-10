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
        private const float TOP_H = 130f; // コンパクトな高さ

        private GUIStyle panelStyle, titleStyle, hintStyle, labelStyle;
        private GUIStyle blockStyle, activeBlockStyle, paletteBlockStyle;
        private bool stylesInitialized = false;

        void Start()
        {
            editor = GetComponent<PointCloudEditor>();
            editorUI = GetComponent<PointCloudEditorUI>();
        }

        private void InitStyles()
        {
            if (stylesInitialized) return;

            Texture2D Tex(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t; }

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = Tex(new Color(0.09f, 0.11f, 0.15f, 0.97f));
            panelStyle.border = new RectOffset(1, 1, 1, 1);

            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = new Color(0.22f, 0.80f, 1f);

            hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Italic };
            hintStyle.normal.textColor = new Color(0.55f, 0.55f, 0.62f);

            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            labelStyle.normal.textColor = new Color(0.88f, 0.88f, 0.92f);

            blockStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
            blockStyle.normal.textColor = Color.white;
            blockStyle.normal.background = Tex(new Color(0.24f, 0.28f, 0.36f));
            blockStyle.wordWrap = false;

            activeBlockStyle = new GUIStyle(blockStyle);
            activeBlockStyle.normal.background = Tex(new Color(0.1f, 0.55f, 0.28f)); // Green highlight

            paletteBlockStyle = new GUIStyle(blockStyle) { fontSize = 12 };
            paletteBlockStyle.normal.background = Tex(new Color(0.17f, 0.20f, 0.27f));

            stylesInitialized = true;
        }

        public void DrawGUI(ref float currentY)
        {
            if (editor == null || editorUI == null) return;
            InitStyles();

            float barW = 450f;
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

            GUI.Label(new Rect(x, y, w, 22f), "📏 2点間距離計測", titleStyle);
            y += 24f;

            // 計測モード切り替えボタン
            bool isMeasuring = editor.activeTool == PointCloudEditor.EditTool.Measure;
            string toggleTxt = isMeasuring ? "計測中... (中クリックで2点)" : "計測ツールをON";
            if (GUI.Button(new Rect(x, y, w, 28f), toggleTxt, isMeasuring ? activeBlockStyle : blockStyle))
            {
                if (isMeasuring)
                {
                    editor.activeTool = PointCloudEditor.EditTool.None;
                    Debug.Log("[DistanceMeasurementUI] 計測ツールをOFFにしました。");
                }
                else
                {
                    editor.activeTool = PointCloudEditor.EditTool.Measure;
                    Debug.Log("[DistanceMeasurementUI] 計測ツールをONにしました。点群上を中クリックして2点を指定してください。");
                }
            }
            y += 32f;

            // 計測距離の表示（unitおよび物理距離併記）
            float localDist = 0f;
            string distStr = "---";
            if (editor.hasMeasurePoint1 && editor.hasMeasurePoint2)
            {
                localDist = Vector3.Distance(editor.measurePoint1, editor.measurePoint2);
                float scaleX = editor.targetRenderer != null ? editor.targetRenderer.transform.localScale.x : 1.0f;
                float worldDistM = localDist * scaleX;
                float worldDistMm = worldDistM * 1000f;

                distStr = $"{localDist:F5} unit ({worldDistMm:F1} mm / {worldDistM:F4} m)";
            }
            GUI.Label(new Rect(x, y, w - 85f, 22f), $"計測距離: {distStr}", labelStyle);

            if (editor.hasMeasurePoint1 || editor.hasMeasurePoint2)
            {
                if (GUI.Button(new Rect(x + w - 80f, y, 80f, 22f), "リセット", paletteBlockStyle))
                {
                    editor.ResetMeasurement();
                }
            }
        }
    }
}
