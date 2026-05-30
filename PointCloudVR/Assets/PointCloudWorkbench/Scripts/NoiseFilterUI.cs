using UnityEngine;
using System.IO;
using System.Threading.Tasks;
using PointCloudWorkbench;

namespace PointCloudWorkbench
{
    /// <summary>
    /// 空中モヤ・浮遊点ノイズ除去フィルタのパラメータ設定とUI描画、プロセス非同期実行を担当するクラス。
    /// </summary>
    public class NoiseFilterUI : MonoBehaviour
    {
        private PointCloudEditor editor;
        
        // White Haze (白モヤ除去) パラメータ
        public bool runWhiteHaze = true; // デフォルトON
        public float whBrightness = 190.0f;
        public float whSaturation = 0.20f;

        // SOR (統計的ノイズ除去) パラメータ
        public bool runSor = true;
        public int sorNb = 20;
        public float sorStd = 1.5f;

        // ROR (半径外れ値除去) パラメータ
        public bool runRor = false; // デフォルトOFF（補助扱い）
        public float rorMul = 3.0f;
        public int rorMin = 8;

        // 低密度フィルタ
        public bool runDensity = false;
        public int densityK = 8;
        public float densityThreshold = 0.0f;

        // CC (局所平面推定ノイズ除去) パラメータ
        public bool runCc = true; // デフォルトON（主役）
        public bool ccUseKnn = true;
        public int ccK = 20;
        public float ccRadius = 0.05f;
        public bool ccRemoveIsolated = false;
        public bool ccUseRelative = true;
        public float ccSigma = 1.0f;
        public float ccError = 0.01f;

        // DBSCAN (小クラスタ除去) パラメータ
        public bool runDbscan = true;
        public float dbscanEps = 4.0f;
        public int dbscanMin = 10;
        public int dbscanCluster = 200;
        public int dbscanTarget = 200000;

        // 動作モード
        public string processMode = "full"; // "full" (全体適用) または "downsample" (プレビュー用)
        public float voxelSize = 0.005f; // ダウンサンプル時のボクセルサイズ

        // 非同期スレッド完了検知用フラグ
        private volatile bool filterFinishedFlag = false;
        private volatile bool filterFailedFlag = false;
        private volatile string asyncErrorMessage = "";
        private NoiseFilterResult asyncResult = null;

        void Start()
        {
            editor = GetComponent<PointCloudEditor>();
            if (editor == null)
            {
                UnityEngine.Debug.LogError("[NoiseFilterUI] PointCloudEditor コンポーネントが見つかりません。");
            }
        }

        void Update()
        {
            // メインスレッドでの非同期解析完了処理 (スレッドセーフ対策)
            if (filterFinishedFlag)
            {
                filterFinishedFlag = false;
                if (asyncResult != null)
                {
                    NoiseFilterManager.Instance.SetResult(asyncResult);
                    NoiseFilterManager.Instance.ApplyPreview(editor.targetRenderer);
                    editor.MarkStatsDirty();
                }
                PointCloudProgressManager.Instance.Complete();
            }

            if (filterFailedFlag)
            {
                filterFailedFlag = false;
                PointCloudProgressManager.Instance.ShowError("空中モヤ・浮遊点ノイズ除去エラー", asyncErrorMessage);
            }
        }

        /// <summary>
        /// PointCloudEditorUI から呼び出され、 OnGUI のスクロールビュー内に項目を描画します。
        /// </summary>
        public void DrawNoiseFilterSection(float width, GUIStyle textStyle, GUIStyle toggleStyle, GUIStyle buttonStyle, GUIStyle activeButtonStyle)
        {
            if (editor == null || editor.targetRenderer == null) return;

            GUILayout.Space(5);

            // --- 1. 処理モード設定 ---
            GUILayout.Label("⚙ 処理モード設定", textStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Full (全体適用)", processMode == "full" ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 2f)))
            {
                processMode = "full";
            }
            GUI.enabled = false; // 点数不一致バグ回避のため一時的に無効化
            if (GUILayout.Button("Downsample (プレビュー)", processMode == "downsample" ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 2f)))
            {
                processMode = "downsample";
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (processMode == "downsample")
            {
                GUILayout.Label($"  ボクセルサイズ: {voxelSize:F4} m", textStyle);
                voxelSize = GUILayout.HorizontalSlider(voxelSize, 0.001f, 0.02f);
            }
            GUILayout.Space(5);

            // --- 1.5. White Haze (白モヤ除去 - デフォルトON) ---
            runWhiteHaze = GUILayout.Toggle(runWhiteHaze, " 空中白モヤ除去 (White Haze) を有効化", toggleStyle);
            if (runWhiteHaze)
            {
                GUILayout.Label($"    最小輝度 (Brightness >=): {whBrightness:F1}", textStyle);
                whBrightness = GUILayout.HorizontalSlider(whBrightness, 100.0f, 255.0f);

                GUILayout.Label($"    最大彩度 (Saturation <=): {whSaturation:F2}", textStyle);
                whSaturation = GUILayout.HorizontalSlider(whSaturation, 0.01f, 1.0f);

                GUILayout.Space(3);
                var prevColor = GUI.contentColor;
                GUI.contentColor = new Color(0.65f, 0.9f, 1.0f);
                GUILayout.Label("    白モヤ候補は水色でプレビューされ、後続の SOR / ROR / 低密度 / CC / DBSCAN の計算対象から除外されます。", textStyle);
                GUI.contentColor = prevColor;
            }
            GUILayout.Space(5);

            // --- 2. CC (平面推定ノイズ除去 - 推奨・主役) ---
            runCc = GUILayout.Toggle(runCc, " 平面推定ノイズ除去 (CC風・推奨) を有効化", toggleStyle);
            if (runCc)
            {
                // 植物点群向けの警告注意メッセージラベル
                var prevColor = GUI.contentColor;
                GUI.contentColor = new Color(0.95f, 0.65f, 0.2f); // オレンジイエロー
                GUILayout.Label("    【注意】局所平面から大きく浮いた点を候補化します。\n    ※ 細い葉先や花を誤消去しやすいため、しきい値設定に注意してください。", textStyle);
                GUI.contentColor = prevColor;

                // 近傍モードトグル
                GUILayout.BeginHorizontal();
                GUILayout.Label("    近傍モード:", textStyle, GUILayout.Width(100));
                bool selectedKnn = GUILayout.Toggle(ccUseKnn, "KNN (近傍点数)", toggleStyle);
                bool selectedRadius = GUILayout.Toggle(!ccUseKnn, "Radius (近傍半径)", toggleStyle);
                if (selectedKnn != ccUseKnn)
                {
                    ccUseKnn = selectedKnn;
                }
                else if (selectedRadius == ccUseKnn)
                {
                    ccUseKnn = !selectedRadius;
                }
                GUILayout.EndHorizontal();

                // 近傍モード値スライダー
                if (ccUseKnn)
                {
                    GUILayout.Label($"      近傍点数 (k): {ccK}", textStyle);
                    ccK = Mathf.RoundToInt(GUILayout.HorizontalSlider(ccK, 3f, 50f));
                }
                else
                {
                    GUILayout.Label($"      近傍半径 (radius): {ccRadius:F3} m", textStyle);
                    ccRadius = GUILayout.HorizontalSlider(ccRadius, 0.005f, 0.2f);
                }

                // しきい値モードトグル
                GUILayout.BeginHorizontal();
                GUILayout.Label("    閾値モード:", textStyle, GUILayout.Width(100));
                bool selectedRel = GUILayout.Toggle(ccUseRelative, "相対シグマ", toggleStyle);
                bool selectedAbs = GUILayout.Toggle(!ccUseRelative, "絶対誤差", toggleStyle);
                if (selectedRel != ccUseRelative)
                {
                    ccUseRelative = selectedRel;
                }
                else if (selectedAbs == ccUseRelative)
                {
                    ccUseRelative = !selectedAbs;
                }
                GUILayout.EndHorizontal();

                // しきい値値スライダー
                if (ccUseRelative)
                {
                    GUILayout.Label($"      標準偏差倍率 (Sigma): {ccSigma:F2}", textStyle);
                    ccSigma = GUILayout.HorizontalSlider(ccSigma, 0.1f, 3.0f);
                }
                else
                {
                    GUILayout.Label($"      絶対誤差閾値 (Error): {ccError:F4} m", textStyle);
                    ccError = GUILayout.HorizontalSlider(ccError, 0.0001f, 0.05f);
                }

                // 孤立点除去トグル
                ccRemoveIsolated = GUILayout.Toggle(ccRemoveIsolated, "    近傍不足の孤立点も除去する", toggleStyle);
            }
            GUILayout.Space(5);

            // --- 3. SOR (統計的外れ値除去) ---
            runSor = GUILayout.Toggle(runSor, " 統計的ノイズ除去 (SOR) を有効化", toggleStyle);
            if (runSor)
            {
                GUILayout.Label($"    隣接点数 (Neighbors): {sorNb}", textStyle);
                sorNb = Mathf.RoundToInt(GUILayout.HorizontalSlider(sorNb, 5f, 50f));

                GUILayout.Label($"    標準偏差倍率 (StdMul): {sorStd:F2}", textStyle);
                sorStd = GUILayout.HorizontalSlider(sorStd, 0.5f, 3.0f);
            }
            GUILayout.Space(5);

            // --- 4. ROR (半径外れ値除去 - 補助) ---
            runRor = GUILayout.Toggle(runRor, " 半径外れ値除去 (ROR) を有効化 (補助)", toggleStyle);
            if (runRor)
            {
                GUILayout.Label($"    検索半径倍率 (RadiusMul): {rorMul:F2}", textStyle);
                rorMul = GUILayout.HorizontalSlider(rorMul, 1.0f, 10.0f);

                GUILayout.Label($"    最小隣接点数 (MinNeighbors): {rorMin}", textStyle);
                rorMin = Mathf.RoundToInt(GUILayout.HorizontalSlider(rorMin, 1f, 30f));
            }
            GUILayout.Space(5);

            // --- 4.5. 低密度フィルタ ---
            runDensity = GUILayout.Toggle(runDensity, " 低密度ノイズ判定を有効化", toggleStyle);
            if (runDensity)
            {
                GUILayout.Label($"    近傍点数 (Density k): {densityK}", textStyle);
                densityK = Mathf.RoundToInt(GUILayout.HorizontalSlider(densityK, 3f, 32f));

                GUILayout.Label($"    低密度閾値: {densityThreshold:F4}", textStyle);
                densityThreshold = GUILayout.HorizontalSlider(densityThreshold, 0.0f, 100.0f);
            }
            GUILayout.Space(5);

            // --- 5. DBSCAN (クラスタノイズ除去) ---
            runDbscan = GUILayout.Toggle(runDbscan, " クラスタノイズ除去 (DBSCAN) を有効化", toggleStyle);
            if (runDbscan)
            {
                GUILayout.Label($"    近傍半径倍率 (EpsMul): {dbscanEps:F2}", textStyle);
                dbscanEps = GUILayout.HorizontalSlider(dbscanEps, 1.0f, 10.0f);

                GUILayout.Label($"    最小近傍点数 (MinPoints): {dbscanMin}", textStyle);
                dbscanMin = Mathf.RoundToInt(GUILayout.HorizontalSlider(dbscanMin, 2f, 50f));

                GUILayout.Label($"    最小クラスタサイズ: {dbscanCluster} 点", textStyle);
                dbscanCluster = Mathf.RoundToInt(GUILayout.HorizontalSlider(dbscanCluster, 10f, 1000f));
            }
            GUILayout.Space(8);

            // --- 5. 実行コントロール ---
            if (GUILayout.Button("🚀 ノイズフィルタ解析を実行", activeButtonStyle, GUILayout.Height(35)))
            {
                RunNoiseFilterAnalysis();
            }

            var mgr = NoiseFilterManager.Instance;
            if (mgr.IsPreviewActive)
            {
                GUILayout.Space(5);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("確定 (Commit)", activeButtonStyle))
                {
                    mgr.CommitRemoval(editor.targetRenderer);
                    editor.MarkStatsDirty();
                }
                if (GUILayout.Button("プレビュークリア", buttonStyle))
                {
                    mgr.ClearPreview(editor.targetRenderer);
                }
                GUILayout.EndHorizontal();
            }

            // 履歴操作 (Undo / Redo)
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            GUI.enabled = mgr.CanUndo;
            if (GUILayout.Button("↩ 元に戻す (Undo)", buttonStyle))
            {
                mgr.Undo(editor.targetRenderer);
                editor.MarkStatsDirty();
            }
            GUI.enabled = mgr.CanRedo;
            if (GUILayout.Button("↪ やり直す (Redo)", buttonStyle))
            {
                mgr.Redo(editor.targetRenderer);
                editor.MarkStatsDirty();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // リセット
            if (GUILayout.Button("🗑 すべてのノイズフィルタを解除", buttonStyle))
            {
                mgr.ResetAllFilterFlags(editor.targetRenderer);
                editor.MarkStatsDirty();
            }

            // --- 6. エクスポート ---
            GUILayout.Space(8);
            if (GUILayout.Button("💾 クリーンアップ済PLYをエクスポート", activeButtonStyle, GUILayout.Height(35)))
            {
                editor.ExportCleanedPoints();
            }
        }

        private void RunNoiseFilterAnalysis()
        {
            if (editor == null || editor.targetRenderer == null) return;
            var loader = editor.targetRenderer.GetComponent<PointCloudLoader>();
            if (loader == null) return;
            string inputPath = loader.GetFilePath();

            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
            {
                UnityEngine.Debug.LogError("[NoiseFilterUI] 点群ファイルが読み込まれていないか、パスが無効です。");
                return;
            }

            string outputDir = Path.Combine(Application.dataPath, "../python_backend/output");
            var pm = PointCloudProgressManager.Instance;
            pm.Start("空中モヤ・浮遊点ノイズ除去", "Pythonプロセスを準備中...");

            // 非同期でPythonバッチ処理を起動
            Task.Run(async () =>
            {
                try
                {
                    var token = pm.CancellationToken;
                    NoiseFilterResult result = await PythonBridge.RunDenoiserAsync(
                        inputPath,
                        outputDir,
                        processMode,
                        voxelSize,
                        runSor, sorNb, sorStd,
                        runRor, rorMul, rorMin,
                        runDensity, densityK, densityThreshold,
                        runCc, ccK, ccSigma, ccError, ccUseKnn, ccRadius, ccRemoveIsolated, ccUseRelative,
                        runDbscan, dbscanEps, dbscanMin, dbscanCluster, dbscanTarget,
                        runWhiteHaze, whBrightness, whSaturation,
                        token
                    );

                    if (!token.IsCancellationRequested)
                    {
                        asyncResult = result;
                        filterFinishedFlag = true;
                    }
                }
                catch (System.OperationCanceledException)
                {
                    UnityEngine.Debug.LogWarning("[NoiseFilterUI] 解析処理がユーザーによってキャンセルされました。");
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogError($"[NoiseFilterUI] 解析処理エラー: {ex.Message}");
                    asyncErrorMessage = ex.Message;
                    filterFailedFlag = true;
                }
            });
        }

        // 凡例表示用スタイルのキャッシュ
        private Texture2D legendBgTexture;
        private Texture2D colorTexture;
        private GUIStyle legendStyle;
        private GUIStyle legendTitleStyle;
        private GUIStyle legendTextStyle;
        private bool legendStylesInitialized = false;

        private void InitializeLegendStyles()
        {
            if (legendStylesInitialized) return;

            legendBgTexture = new Texture2D(1, 1);
            legendBgTexture.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.16f, 0.85f)); // ダークインディゴ半透明
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

        void OnGUI()
        {
            // ノイズプレビューが有効なときのみ表示
            if (NoiseFilterManager.Instance == null || !NoiseFilterManager.Instance.IsPreviewActive)
            {
                return;
            }

            InitializeLegendStyles();

            // 画面左下に配置する（十分な大きさにする、凡例1つ増えたため高さを210fに拡張）
            float width = 340f;
            float height = 210f;
            float posX = 20f;
            float posY = Screen.height - height - 20f;

            GUILayout.BeginArea(new Rect(posX, posY, width, height), legendStyle);

            GUILayout.Label("🧹 除去対象ノイズ凡例 (プレビュー)", legendTitleStyle);
            GUILayout.Space(8);

            DrawLegendItem(new Color(0.0f, 0.85f, 1.0f, 1.0f), "空中白モヤ (White Haze)：水色");
            DrawLegendItem(new Color(1.0f, 0.12f, 0.12f, 1.0f), "SOR (統計的ノイズ除去)：赤");
            DrawLegendItem(new Color(1.0f, 0.55f, 0.0f, 1.0f), "ROR (半径外れ値除去)：橙");
            DrawLegendItem(new Color(0.63f, 0.12f, 0.9f, 1.0f), "低密度ノイズ除去：紫");
            DrawLegendItem(new Color(1.0f, 0.86f, 0.0f, 1.0f), "クラスタノイズ (DBSCAN)：黄");
            DrawLegendItem(new Color(1.0f, 0.0f, 0.5f, 1.0f), "平面推定 (CC風)ノイズ：ピンク");

            GUILayout.EndArea();
        }

        private void DrawLegendItem(Color color, string label)
        {
            GUILayout.BeginHorizontal();
            
            // 色を示す四角形を描画
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
            if (legendBgTexture != null) Destroy(legendBgTexture);
            if (colorTexture != null) Destroy(colorTexture);
        }
    }
}
