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
        private NoiseFilterUI noiseFilterUI;

        // D&D 状態
        private string draggingBlockType = null;
        private int    draggingSourceIndex = -1;
        private Vector2 dragMouseOffset;

        // 選択
        private int selectedBlockIndex = -1;

        // 右クリックメニュー
        private int     contextMenuBlockIndex = -1;
        private Vector2 contextMenuPos;
        private bool    showContextMenu = false;

        // コピーバッファ
        private string copiedBlockType = null;

        // スタイル
        private GUIStyle panelStyle, titleStyle, hintStyle, labelStyle;
        private GUIStyle blockStyle, activeBlockStyle, paletteBlockStyle;
        private bool stylesInitialized = false;

        // レイアウト定数
        // 左パネル(450px)と右パネル(400px)の間に配置する
        private const float BAR_X      = 480f;     // 左パネル(450) + 余白(30)
        private const float BAR_Y      = 15f;
        private const float RIGHT_W    = 400f;     // 右パネル幅
        private const float PAL_W      = 175f;     // パレット列幅 (文字拡大に合わせて広げる)
        private const float TOP_H      = 160f;     // 上段高さ(パレット+レーン) (130->160へ拡大)
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
            noiseFilterUI = GetComponent<NoiseFilterUI>();
        }

        void Update() => HandleKeyboard();

        // =========================================================
        // キーボードショートカット
        // =========================================================
        private void HandleKeyboard()
        {
            if (noiseFilterUI?.Params?.customPipeline == null) return;
            var pl = noiseFilterUI.Params.customPipeline;

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
            panelStyle.normal.background = Tex(new Color(0.09f, 0.11f, 0.15f, 0.97f));
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
        // OnGUI エントリ
        // =========================================================
        void OnGUI()
        {
            if (editor == null || noiseFilterUI == null) return;
            InitStyles();

            float barW = Screen.width - BAR_X - RIGHT_W - 30f; // Calculate space in betweenleft and right panels
            float barH = TOP_H + PARAM_H;
            Rect bar = new Rect(BAR_X, BAR_Y, barW, barH);

            // バー背景
            GUI.Box(bar, "", panelStyle);

            // 上段: パレット + レーン
            DrawPalette(bar);
            DrawLane(bar);

            // 区切り線
            DrawDivider(new Rect(BAR_X + 5, BAR_Y + TOP_H, barW - 10, 1));

            // 下段: パラメータ（バー内に統合、外部には一切出ない）
            DrawParamPanel(new Rect(BAR_X, BAR_Y + TOP_H + 1, barW, PARAM_H - 1));

            // ドラッグゴースト / コンテキストメニュー
            DrawDragGhost();
            DrawContextMenu();

            // このUIバーの外部をクリックした際にブロック選択を解除する
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && !bar.Contains(ev.mousePosition))
            {
                selectedBlockIndex = -1;
            }
        }

        // =========================================================
        // パレット描画（絶対座標）
        // =========================================================
        private void DrawPalette(Rect bar)
        {
            float px   = bar.x + 5f;
            float py   = bar.y + 4f;
            float bW   = PAL_W - 8f;
            float titleH = 22f; // タイトル高さを文字拡大に合わせて少し広げる
            // ボタン高さ: 上段高さから title と余白を引いてボタン数で割る
            float usable = TOP_H - titleH - 8f - (AvailableTypes.Length - 1) * 3f;
            float bH = Mathf.Floor(usable / AvailableTypes.Length);
            bH = Mathf.Clamp(bH, 18f, 32f); // パレットボタンの高さ制限を拡大 (14-22 -> 18-32)

            GUI.Label(new Rect(px, py, bW, titleH), "パレット", titleStyle);
            py += titleH + 2f;

            for (int i = 0; i < AvailableTypes.Length; i++)
            {
                string type = AvailableTypes[i];
                string lbl  = D(type);
                Rect r = new Rect(px, py + i * (bH + 2f), bW, bH);

                if (GUI.Button(r, lbl, paletteBlockStyle))
                {
                    EnsurePipeline();
                    noiseFilterUI.Params.customPipeline.Add(MakeStep(type));
                    selectedBlockIndex = noiseFilterUI.Params.customPipeline.Count - 1;
                }

                var ev = Event.current;
                if (ev.type == EventType.MouseDown && r.Contains(ev.mousePosition) && ev.button == 0)
                {
                    draggingBlockType  = type;
                    draggingSourceIndex = -1;
                    dragMouseOffset    = ev.mousePosition - r.min;
                    ev.Use();
                }
            }
        }

        // =========================================================
        // レーン描画（絶対座標）
        // =========================================================
        private void DrawLane(Rect bar)
        {
            const float p      = 5f;
            const float titleH = 22f; // タイトル高さを文字拡大に合わせる
            const float btnW   = 85f;  // ボタン幅を文字拡大に合わせる
            const float modeW  = 120f; // モード選択ボタンの幅を広げる

            float lx   = bar.x + PAL_W + 6f;
            float btnX = bar.x + bar.width - p - btnW;
            float modeX = btnX - 6f - modeW;

            // タイトル
            GUI.Label(new Rect(lx, bar.y + p + 2f, modeX - lx - 4, titleH),
                "順次実行レーン  (パレット D&D で追加 / ブロック D&D で並べ替え)", titleStyle);

            // モードボタン (高さ 28f に拡大してフォントに合わせる)
            if (noiseFilterUI.Params != null)
            {
                bool isFull = noiseFilterUI.Params.processMode == "full";
                if (GUI.Button(new Rect(modeX, bar.y + p, modeW, 28f), isFull ? "全体適用" : "ダウンサンプル", activeBlockStyle))
                    noiseFilterUI.Params.processMode = isFull ? "downsample" : "full";
            }
            // 実行ボタン (高さ 28f に拡大してフォントに合わせる)
            if (GUI.Button(new Rect(btnX, bar.y + p, btnW, 28f), "▶ 実行", activeBlockStyle))
                noiseFilterUI.RunNoiseFilterAnalysis();

            // レーン背景
            float laneY = bar.y + p + titleH + 6f;
            float laneH = bar.y + TOP_H - laneY - p;
            float laneW = btnX + btnW - lx; // 右端までレーンを伸ばす
            Rect lane = new Rect(lx, laneY, laneW, laneH);
            GUI.Box(lane, "", GUI.skin.textField);

            EnsurePipeline();
            var pl = noiseFilterUI.Params.customPipeline;

            // ブロックサイズを大きくして文字潰れを防ぐ
            const float bW       = 130f; // ブロック幅を 100 -> 130f に拡張
            const float bSpacing = 22f;  // 間隔を 16 -> 22f に拡張
            float sx = lane.x + 5f;
            float bH = Mathf.Min(50f, lane.height - 10f); // ブロック高さを 32 -> 50f に拡張
            float sy = lane.y + (lane.height - bH) / 2f;

            bool clickedBlock = false;
            var  ev = Event.current;

            for (int i = 0; i < pl.Count; i++)
            {
                float bx = sx + i * (bW + bSpacing);

                // レーン右端を超えたら "…" を出して打ち切り
                if (bx + bW > lane.x + lane.width - 16f)
                {
                    GUI.Label(new Rect(lane.x + lane.width - 15f, sy + (bH - 18f) / 2f, 15f, 18f), "…", titleStyle);
                    break;
                }

                var step = pl[i];
                Rect br   = new Rect(bx, sy, bW, bH);
                string txt = D(step.name) + (step.enabled ? "" : "\n(無効)");
                bool selected = selectedBlockIndex == i;
                GUI.Box(br, txt, selected ? activeBlockStyle : blockStyle);

                // 矢印（次ブロックが収まる場合のみ表示）
                bool nextFits = i < pl.Count - 1 &&
                                bx + bW + bSpacing + bW <= lane.x + lane.width - 16f;
                if (nextFits)
                    GUI.Label(new Rect(bx + bW + 4f, sy + (bH - 18f) / 2f, 14f, 18f), "▶", titleStyle);

                // クリック / 右クリック / D&D 開始
                if (ev.type == EventType.MouseDown && br.Contains(ev.mousePosition))
                {
                    clickedBlock = true;
                    if (ev.button == 0)
                    {
                        selectedBlockIndex  = i;
                        draggingBlockType   = step.name;
                        draggingSourceIndex = i;
                        dragMouseOffset     = ev.mousePosition - br.min;
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
            if (!clickedBlock && ev.type == EventType.MouseDown && lane.Contains(ev.mousePosition))
            {
                selectedBlockIndex = -1;
                ev.Use();
            }

            // ドロップ処理
            if (ev.type == EventType.MouseUp && draggingBlockType != null)
            {
                if (lane.Contains(ev.mousePosition))
                {
                    const float targetBlockWidth = 130f;
                    const float targetSpacing = 22f;
                    
                    // レーン左端余白(5f)を考慮し、クリックされた位置からインデックスを計算
                    float relativeX = ev.mousePosition.x - (lane.x + 5f);
                    int ins = Mathf.Clamp(
                        Mathf.RoundToInt(relativeX / (targetBlockWidth + targetSpacing)),
                        0, pl.Count);

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

                draggingBlockType   = null;
                draggingSourceIndex = -1;
                ev.Use();
            }
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
            float x  = r.x + 8f;
            float y  = r.y + 4f;
            float lh = 21f;

            // --- 行1: タイトル + 有効化 + 除外 ---
            GUI.Label(new Rect(x, y, 175f, 22f), $"⚙ {D(step.name)}", titleStyle);
            step.enabled          = GUI.Toggle(new Rect(x + 190f, y + 2f, 85f, 20f), step.enabled, " 有効");
            step.excludeFromNext  = GUI.Toggle(new Rect(x + 285f, y + 2f, 230f, 20f), step.excludeFromNext, " 次段から除外 (exclude)");
            y += lh + 4f;

            // --- 行2〜: スライダー (左右2カラム) ---
            float colW = (r.width - 20f) / 2f;
            float lw   = 175f; // Label width expanded for larger font
            float vw   = 42f;
            float sw   = Mathf.Max(colW - lw - 15f, 30f);

            GUI.enabled = step.enabled;

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
                dn.k         = SliderInt(x,        y, lw, sw, $"近傍点数 k: {dn.k}",          dn.k,         3, 32);
                dn.threshold = Slider   (x + colW, y, lw, sw, $"低密度閾値: {dn.threshold:F2}", dn.threshold, 0f, 100f);
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
                    cc.radius = Slider   (x + colW, y, lw, sw, $"半径: {cc.radius:F3} m",       cc.radius, 0.005f, 0.2f);

                // 2行目: 相対/絶対 トグル + 値スライダー
                y += lh + 2f;
                cc.useRelative = GUI.Toggle(new Rect(x,         y, 115f, 20f), cc.useRelative, " 相対σ");
                cc.useRelative = !GUI.Toggle(new Rect(x + 120f, y, 125f, 20f), !cc.useRelative, " 絶対誤差");

                if (cc.useRelative)
                    cc.sigma = Slider(x + colW, y, lw, sw, $"Sigma: {cc.sigma:F2}", cc.sigma, 0.1f, 3f);
                else
                    cc.error = Slider(x + colW, y, lw, sw, $"Error: {cc.error:F4}", cc.error, 0.0001f, 0.05f);

                // 3行目: 孤立点トグル
                y += lh + 2f;
                cc.removeIsolated = GUI.Toggle(new Rect(x, y, 180f, 20f), cc.removeIsolated, " 孤立点も除去");
            }

            GUI.enabled = true;
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
            if (draggingBlockType == null) return;
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
        private void EnsurePipeline()
        {
            if (noiseFilterUI.Params.customPipeline == null)
                noiseFilterUI.Params.customPipeline = noiseFilterUI.Params.GetPipeline();
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
                default:           return new FilterStepConfig { name = t, enabled = true };
            }
        }

        private string D(string t) => DispNames.ContainsKey(t) ? DispNames[t] : t;
    }
}
