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
        private NoiseFilterParams _params = new NoiseFilterParams();

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

            DrawModeSection(width, textStyle, buttonStyle, activeButtonStyle);
            DrawWhiteHazeSection(textStyle, toggleStyle);
            DrawCcSection(textStyle, toggleStyle);
            DrawSorSection(textStyle, toggleStyle);
            DrawRorSection(textStyle, toggleStyle);
            DrawDensitySection(textStyle, toggleStyle);
            DrawDbscanSection(textStyle, toggleStyle);
            DrawControlButtons(buttonStyle, activeButtonStyle);
        }

        private void DrawModeSection(float width, GUIStyle textStyle, GUIStyle buttonStyle, GUIStyle activeButtonStyle)
        {
            GUILayout.Label("⚙ 処理モード設定", textStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Full (全体適用)", _params.processMode == "full" ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 2f)))
            {
                _params.processMode = "full";
            }
            GUI.enabled = false; // 点数不一致バグ回避のため一時的に無効化
            if (GUILayout.Button("Downsample (プレビュー)", _params.processMode == "downsample" ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 2f)))
            {
                _params.processMode = "downsample";
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_params.processMode == "downsample")
            {
                GUILayout.Label($"  ボクセルサイズ: {_params.voxelSize:F4} m", textStyle);
                _params.voxelSize = GUILayout.HorizontalSlider(_params.voxelSize, 0.001f, 0.02f);
            }
            GUILayout.Space(5);
        }

        private void DrawWhiteHazeSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            _params.whiteHaze.enabled = GUILayout.Toggle(_params.whiteHaze.enabled, " 空中白モヤ除去 (White Haze) を有効化", toggleStyle);
            if (_params.whiteHaze.enabled)
            {
                GUILayout.Label($"    最小輝度 (Brightness >=): {_params.whiteHaze.brightness:F1}", textStyle);
                _params.whiteHaze.brightness = GUILayout.HorizontalSlider(_params.whiteHaze.brightness, 100.0f, 255.0f);

                GUILayout.Label($"    最大彩度 (Saturation <=): {_params.whiteHaze.saturation:F2}", textStyle);
                _params.whiteHaze.saturation = GUILayout.HorizontalSlider(_params.whiteHaze.saturation, 0.01f, 1.0f);

                GUILayout.Space(3);
                var prevColor = GUI.contentColor;
                GUI.contentColor = new Color(0.65f, 0.9f, 1.0f);
                GUILayout.Label("    白モヤ候補は水色でプレビューされ、後続の SOR / ROR / 低密度 / CC / DBSCAN の計算対象から除外されます。", textStyle);
                GUI.contentColor = prevColor;
            }
            GUILayout.Space(5);
        }

        private void DrawCcSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            _params.cc.enabled = GUILayout.Toggle(_params.cc.enabled, " 平面推定ノイズ除去 (CC風・推奨) を有効化", toggleStyle);
            if (_params.cc.enabled)
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = new Color(0.95f, 0.65f, 0.2f); // オレンジイエロー
                GUILayout.Label("    【注意】局所平面から大きく浮いた点を候補化します。\n    ※ 細い葉先や花を誤消去しやすいため、しきい値設定に注意してください。", textStyle);
                GUI.contentColor = prevColor;

                GUILayout.BeginHorizontal();
                GUILayout.Label("    近傍モード:", textStyle, GUILayout.Width(100));
                bool selectedKnn = GUILayout.Toggle(_params.cc.useKnn, "KNN (近傍点数)", toggleStyle);
                bool selectedRadius = GUILayout.Toggle(!_params.cc.useKnn, "Radius (近傍半径)", toggleStyle);
                if (selectedKnn != _params.cc.useKnn)
                {
                    _params.cc.useKnn = selectedKnn;
                }
                else if (selectedRadius == _params.cc.useKnn)
                {
                    _params.cc.useKnn = !selectedRadius;
                }
                GUILayout.EndHorizontal();

                if (_params.cc.useKnn)
                {
                    GUILayout.Label($"      近傍点数 (k): {_params.cc.k}", textStyle);
                    _params.cc.k = Mathf.RoundToInt(GUILayout.HorizontalSlider(_params.cc.k, 3f, 50f));
                }
                else
                {
                    GUILayout.Label($"      近傍半径 (radius): {_params.cc.radius:F3} m", textStyle);
                    _params.cc.radius = GUILayout.HorizontalSlider(_params.cc.radius, 0.005f, 0.2f);
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("    閾値モード:", textStyle, GUILayout.Width(100));
                bool selectedRel = GUILayout.Toggle(_params.cc.useRelative, "相対シグマ", toggleStyle);
                bool selectedAbs = GUILayout.Toggle(!_params.cc.useRelative, "絶対誤差", toggleStyle);
                if (selectedRel != _params.cc.useRelative)
                {
                    _params.cc.useRelative = selectedRel;
                }
                else if (selectedAbs == _params.cc.useRelative)
                {
                    _params.cc.useRelative = !selectedAbs;
                }
                GUILayout.EndHorizontal();

                if (_params.cc.useRelative)
                {
                    GUILayout.Label($"      標準偏差倍率 (Sigma): {_params.cc.sigma:F2}", textStyle);
                    _params.cc.sigma = GUILayout.HorizontalSlider(_params.cc.sigma, 0.1f, 3.0f);
                }
                else
                {
                    GUILayout.Label($"      絶対誤差閾値 (Error): {_params.cc.error:F4} m", textStyle);
                    _params.cc.error = GUILayout.HorizontalSlider(_params.cc.error, 0.0001f, 0.05f);
                }

                _params.cc.removeIsolated = GUILayout.Toggle(_params.cc.removeIsolated, "    近傍不足の孤立点も除去する", toggleStyle);
            }
            GUILayout.Space(5);
        }

        private void DrawSorSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            _params.sor.enabled = GUILayout.Toggle(_params.sor.enabled, " 統計的ノイズ除去 (SOR) を有効化", toggleStyle);
            if (_params.sor.enabled)
            {
                GUILayout.Label($"    隣接点数 (Neighbors): {_params.sor.nb}", textStyle);
                _params.sor.nb = Mathf.RoundToInt(GUILayout.HorizontalSlider(_params.sor.nb, 5f, 50f));

                GUILayout.Label($"    標準偏差倍率 (StdMul): {_params.sor.std:F2}", textStyle);
                _params.sor.std = GUILayout.HorizontalSlider(_params.sor.std, 0.5f, 3.0f);
            }
            GUILayout.Space(5);
        }

        private void DrawRorSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            _params.ror.enabled = GUILayout.Toggle(_params.ror.enabled, " 半径外れ値除去 (ROR) を有効化 (補助)", toggleStyle);
            if (_params.ror.enabled)
            {
                GUILayout.Label($"    検索半径倍率 (RadiusMul): {_params.ror.mul:F2}", textStyle);
                _params.ror.mul = GUILayout.HorizontalSlider(_params.ror.mul, 1.0f, 10.0f);

                GUILayout.Label($"    最小隣接点数 (MinNeighbors): {_params.ror.min}", textStyle);
                _params.ror.min = Mathf.RoundToInt(GUILayout.HorizontalSlider(_params.ror.min, 1f, 30f));
            }
            GUILayout.Space(5);
        }

        private void DrawDensitySection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            _params.density.enabled = GUILayout.Toggle(_params.density.enabled, " 低密度ノイズ判定を有効化", toggleStyle);
            if (_params.density.enabled)
            {
                GUILayout.Label($"    近傍点数 (Density k): {_params.density.k}", textStyle);
                _params.density.k = Mathf.RoundToInt(GUILayout.HorizontalSlider(_params.density.k, 3f, 32f));

                GUILayout.Label($"    低密度閾値: {_params.density.threshold:F4}", textStyle);
                _params.density.threshold = GUILayout.HorizontalSlider(_params.density.threshold, 0.0f, 100.0f);
            }
            GUILayout.Space(5);
        }

        private void DrawDbscanSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            _params.dbscan.enabled = GUILayout.Toggle(_params.dbscan.enabled, " クラスタノイズ除去 (DBSCAN) を有効化", toggleStyle);
            if (_params.dbscan.enabled)
            {
                GUILayout.Label($"    近傍半径倍率 (EpsMul): {_params.dbscan.eps:F2}", textStyle);
                _params.dbscan.eps = GUILayout.HorizontalSlider(_params.dbscan.eps, 1.0f, 10.0f);

                GUILayout.Label($"    最小近傍点数 (MinPoints): {_params.dbscan.min}", textStyle);
                _params.dbscan.min = Mathf.RoundToInt(GUILayout.HorizontalSlider(_params.dbscan.min, 2f, 50f));

                GUILayout.Label($"    最小クラスタサイズ: {_params.dbscan.cluster} 点", textStyle);
                _params.dbscan.cluster = Mathf.RoundToInt(GUILayout.HorizontalSlider(_params.dbscan.cluster, 10f, 1000f));
            }
            GUILayout.Space(8);
        }

        private void DrawControlButtons(GUIStyle buttonStyle, GUIStyle activeButtonStyle)
        {
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

            if (GUILayout.Button("🗑 すべてのノイズフィルタを解除", buttonStyle))
            {
                mgr.ResetAllFilterFlags(editor.targetRenderer);
                editor.MarkStatsDirty();
            }

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
                        _params,
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
            if (NoiseFilterManager.Instance == null || !NoiseFilterManager.Instance.IsPreviewActive || editor == null || editor.targetRenderer == null)
            {
                return;
            }

            InitializeLegendStyles();

            // 現在プレビュー状態にある各理由ごとの点数をリアルタイム集計
            int countSor = 0;
            int countRor = 0;
            int countDensity = 0;
            int countDbscan = 0;
            int countCc = 0;
            int countWhiteHaze = 0;

            PointData[] points = editor.targetRenderer.GetPointData();
            if (points != null)
            {
                for (int i = 0; i < points.Length; i++)
                {
                    int label = points[i].label;
                    if ((label & NoiseFilterManager.NOISE_CANDIDATE_BIT) != 0)
                    {
                        int reason = (label & NoiseFilterManager.NOISE_REASON_MASK) >> NoiseFilterManager.NOISE_REASON_SHIFT;
                        if (reason == 1)      countSor++;
                        else if (reason == 2) countRor++;
                        else if (reason == 3) countDensity++;
                        else if (reason == 4) countDbscan++;
                        else if (reason == 5) countCc++;
                        else if (reason == 7) countWhiteHaze++;
                    }
                }
            }

            // 画面左下に配置する（十分な大きさにする、凡例1つ増え、点数が入るため幅を400f、高さを210fに）
            float width = 400f;
            float height = 210f;
            float posX = 20f;
            float posY = Screen.height - height - 20f;

            GUILayout.BeginArea(new Rect(posX, posY, width, height), legendStyle);

            GUILayout.Label("🧹 除去対象ノイズ凡例 (プレビュー)", legendTitleStyle);
            GUILayout.Space(8);

            DrawLegendItem(new Color(0.0f, 0.85f, 1.0f, 1.0f), $"空中白モヤ (White Haze)：水色 ({countWhiteHaze:N0} 点)");
            DrawLegendItem(new Color(1.0f, 0.12f, 0.12f, 1.0f), $"SOR (統計的ノイズ除去)：赤 ({countSor:N0} 点)");
            DrawLegendItem(new Color(1.0f, 0.55f, 0.0f, 1.0f), $"ROR (半径外れ値除去)：橙 ({countRor:N0} 点)");
            DrawLegendItem(new Color(0.63f, 0.12f, 0.9f, 1.0f), $"低密度ノイズ除去：紫 ({countDensity:N0} 点)");
            DrawLegendItem(new Color(1.0f, 0.86f, 0.0f, 1.0f), $"クラスタノイズ (DBSCAN)：黄 ({countDbscan:N0} 点)");
            DrawLegendItem(new Color(1.0f, 0.0f, 0.5f, 1.0f), $"平面推定 (CC風)ノイズ：ピンク ({countCc:N0} 点)");

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
