using System;
using System.Threading;
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
        private PointCloudOperation activeOperation;
        public NoiseFilterParams Params = new NoiseFilterParams();


        void Start()
        {
            editor = GetComponent<PointCloudEditor>();
            if (editor == null)
            {
                UnityEngine.Debug.LogWarning("[RecoverableOperationError] [NoiseFilterUI] PointCloudEditor コンポーネントが見つかりません。");
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
            GUILayout.Label("処理モード設定", textStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Full (全体適用)", Params.processMode == "full" ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 2f)))
            {
                Params.processMode = "full";
            }
            bool previousEnabled = GUI.enabled;
            GUI.enabled = false; // 点数不一致バグ回避のため一時的に無効化
            if (GUILayout.Button("Downsample (プレビュー)", Params.processMode == "downsample" ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 2f)))
            {
                Params.processMode = "downsample";
            }
            GUI.enabled = previousEnabled;
            GUILayout.EndHorizontal();

            if (Params.processMode == "downsample")
            {
                GUILayout.Label($"  ボクセルサイズ: {Params.voxelSize:F1} mm", textStyle);
                Params.voxelSize = GUILayout.HorizontalSlider(Params.voxelSize, 1f, 20f);
            }
            GUILayout.Space(5);
        }

        private void DrawWhiteHazeSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            Params.whiteHaze.enabled = GUILayout.Toggle(Params.whiteHaze.enabled, " 空中白モヤ除去 (White Haze) を有効化", toggleStyle);
            if (Params.whiteHaze.enabled)
            {
                GUILayout.Label($"    最小輝度 (Brightness >=): {Params.whiteHaze.brightness:F1}", textStyle);
                Params.whiteHaze.brightness = GUILayout.HorizontalSlider(Params.whiteHaze.brightness, 100.0f, 255.0f);

                GUILayout.Label($"    最大彩度 (Saturation <=): {Params.whiteHaze.saturation:F2}", textStyle);
                Params.whiteHaze.saturation = GUILayout.HorizontalSlider(Params.whiteHaze.saturation, 0.01f, 1.0f);

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
            Params.cc.enabled = GUILayout.Toggle(Params.cc.enabled, " 平面推定ノイズ除去 (CC風・推奨) を有効化", toggleStyle);
            if (Params.cc.enabled)
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = new Color(0.95f, 0.65f, 0.2f); // オレンジイエロー
                GUILayout.Label("    【注意】局所平面から大きく浮いた点を候補化します。\n    ※ 細い葉先や花を誤消去しやすいため、しきい値設定に注意してください。", textStyle);
                GUI.contentColor = prevColor;

                GUILayout.BeginHorizontal();
                GUILayout.Label("    近傍モード:", textStyle, GUILayout.Width(100));
                bool selectedKnn = GUILayout.Toggle(Params.cc.useKnn, "KNN (近傍点数)", toggleStyle);
                bool selectedRadius = GUILayout.Toggle(!Params.cc.useKnn, "Radius (近傍半径)", toggleStyle);
                if (selectedKnn != Params.cc.useKnn)
                {
                    Params.cc.useKnn = selectedKnn;
                }
                else if (selectedRadius == Params.cc.useKnn)
                {
                    Params.cc.useKnn = !selectedRadius;
                }
                GUILayout.EndHorizontal();

                if (Params.cc.useKnn)
                {
                    GUILayout.Label($"      近傍点数 (k): {Params.cc.k}", textStyle);
                    Params.cc.k = Mathf.RoundToInt(GUILayout.HorizontalSlider(Params.cc.k, 3f, 50f));
                }
                else
                {
                    GUILayout.Label($"      近傍半径 (radius): {Params.cc.radius:F1} mm", textStyle);
                    Params.cc.radius = GUILayout.HorizontalSlider(Params.cc.radius, 5f, 200f);
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("    閾値モード:", textStyle, GUILayout.Width(100));
                bool selectedRel = GUILayout.Toggle(Params.cc.useRelative, "相対シグマ", toggleStyle);
                bool selectedAbs = GUILayout.Toggle(!Params.cc.useRelative, "絶対誤差", toggleStyle);
                if (selectedRel != Params.cc.useRelative)
                {
                    Params.cc.useRelative = selectedRel;
                }
                else if (selectedAbs == Params.cc.useRelative)
                {
                    Params.cc.useRelative = !selectedAbs;
                }
                GUILayout.EndHorizontal();

                if (Params.cc.useRelative)
                {
                    GUILayout.Label($"      標準偏差倍率 (Sigma): {Params.cc.sigma:F2}", textStyle);
                    Params.cc.sigma = GUILayout.HorizontalSlider(Params.cc.sigma, 0.1f, 3.0f);
                }
                else
                {
                    GUILayout.Label($"      絶対誤差閾値 (Error): {Params.cc.error:F1} mm", textStyle);
                    Params.cc.error = GUILayout.HorizontalSlider(Params.cc.error, 0.1f, 50f);
                }

                Params.cc.removeIsolated = GUILayout.Toggle(Params.cc.removeIsolated, "    近傍不足の孤立点も除去する", toggleStyle);
            }
            GUILayout.Space(5);
        }

        private void DrawSorSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            Params.sor.enabled = GUILayout.Toggle(Params.sor.enabled, " 統計的ノイズ除去 (SOR) を有効化", toggleStyle);
            if (Params.sor.enabled)
            {
                GUILayout.Label($"    隣接点数 (Neighbors): {Params.sor.nb}", textStyle);
                Params.sor.nb = Mathf.RoundToInt(GUILayout.HorizontalSlider(Params.sor.nb, 5f, 50f));

                GUILayout.Label($"    標準偏差倍率 (StdMul): {Params.sor.std:F2}", textStyle);
                Params.sor.std = GUILayout.HorizontalSlider(Params.sor.std, 0.5f, 3.0f);
            }
            GUILayout.Space(5);
        }

        private void DrawRorSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            Params.ror.enabled = GUILayout.Toggle(Params.ror.enabled, " 半径外れ値除去 (ROR) を有効化 (補助)", toggleStyle);
            if (Params.ror.enabled)
            {
                GUILayout.Label($"    検索半径倍率 (RadiusMul): {Params.ror.mul:F2}", textStyle);
                Params.ror.mul = GUILayout.HorizontalSlider(Params.ror.mul, 1.0f, 10.0f);

                GUILayout.Label($"    最小隣接点数 (MinNeighbors): {Params.ror.min}", textStyle);
                Params.ror.min = Mathf.RoundToInt(GUILayout.HorizontalSlider(Params.ror.min, 1f, 30f));
            }
            GUILayout.Space(5);
        }

        private void DrawDensitySection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            Params.density.enabled = GUILayout.Toggle(Params.density.enabled, " 低密度ノイズ判定を有効化", toggleStyle);
            if (Params.density.enabled)
            {
                GUILayout.Label($"    近傍点数 (Density k): {Params.density.k}", textStyle);
                Params.density.k = Mathf.RoundToInt(GUILayout.HorizontalSlider(Params.density.k, 3f, 32f));

                GUILayout.Label($"    低密度閾値: {Params.density.threshold:F4} 1/mm", textStyle);
                Params.density.threshold = GUILayout.HorizontalSlider(Params.density.threshold, 0.0f, 100.0f);
            }
            GUILayout.Space(5);
        }

        private void DrawDbscanSection(GUIStyle textStyle, GUIStyle toggleStyle)
        {
            Params.dbscan.enabled = GUILayout.Toggle(Params.dbscan.enabled, " クラスタノイズ除去 (DBSCAN) を有効化", toggleStyle);
            if (Params.dbscan.enabled)
            {
                GUILayout.Label($"    近傍半径倍率 (EpsMul): {Params.dbscan.eps:F2}", textStyle);
                Params.dbscan.eps = GUILayout.HorizontalSlider(Params.dbscan.eps, 1.0f, 10.0f);

                GUILayout.Label($"    最小近傍点数 (MinPoints): {Params.dbscan.min}", textStyle);
                Params.dbscan.min = Mathf.RoundToInt(GUILayout.HorizontalSlider(Params.dbscan.min, 2f, 50f));

                GUILayout.Label($"    最小クラスタサイズ: {Params.dbscan.cluster} 点", textStyle);
                Params.dbscan.cluster = Mathf.RoundToInt(GUILayout.HorizontalSlider(Params.dbscan.cluster, 10f, 1000f));
            }
            GUILayout.Space(8);
        }

        private void DrawControlButtons(GUIStyle buttonStyle, GUIStyle activeButtonStyle)
        {
            if (GUILayout.Button("ノイズフィルタ解析を実行", activeButtonStyle, GUILayout.Height(35)))
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
                    if (mgr.CommitRemoval(editor.targetRenderer)) editor.MarkStatsDirty();
                    else PointCloudProgressManager.Instance.ShowError("ノイズ確定", MutationFailure(mgr, "点群ラベルを更新できませんでした。"));
                }
                if (GUILayout.Button("プレビュークリア", buttonStyle))
                {
                    if (!mgr.ClearPreview(editor.targetRenderer))
                        PointCloudProgressManager.Instance.ShowError("ノイズプレビュー", MutationFailure(mgr, "プレビューを解除できませんでした。"));
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && editor.CanAnnotationUndo;
            if (GUILayout.Button("↩ 直前の点群編集を元に戻す", buttonStyle))
            {
                if (editor.AnnotationUndo()) editor.MarkStatsDirty();
                else PointCloudProgressManager.Instance.ShowError("点群編集を元に戻す", "直前の点群編集を適用できませんでした。状態を確認して再試行してください。");
            }
            GUI.enabled = previousEnabled && editor.CanAnnotationRedo;
            if (GUILayout.Button("↪ 点群編集をやり直す", buttonStyle))
            {
                if (editor.AnnotationRedo()) editor.MarkStatsDirty();
                else PointCloudProgressManager.Instance.ShowError("点群編集をやり直す", "直前に元へ戻した点群編集を適用できませんでした。状態を確認して再試行してください。");
            }
            GUI.enabled = previousEnabled;
            GUILayout.EndHorizontal();

            if (GUILayout.Button("すべてのノイズフィルタを解除", buttonStyle))
            {
                if (mgr.ResetAllFilterFlags(editor.targetRenderer)) editor.MarkStatsDirty();
                else PointCloudProgressManager.Instance.ShowError("ノイズ状態リセット", MutationFailure(mgr, "ノイズ状態をリセットできませんでした。"));
            }

            GUILayout.Space(8);
            if (GUILayout.Button("クリーンアップ済PLYをエクスポート", activeButtonStyle, GUILayout.Height(35)))
            {
                editor.ExportCleanedPoints();
            }
        }

        private static string MutationFailure(NoiseFilterManager manager, string fallback)
        {
            return string.IsNullOrWhiteSpace(manager.LastMutationFailure) ? fallback : manager.LastMutationFailure;
        }

        public void RunNoiseFilterAnalysis()
        {
            _ = RunNoiseFilterAnalysisAsync();
        }

        private async Task RunNoiseFilterAnalysisAsync()
        {
            if (editor == null || editor.targetRenderer == null) return;
            PointCloudLoader loader = editor.targetRenderer.GetComponent<PointCloudLoader>();
            string inputPath = loader != null && !string.IsNullOrEmpty(loader.CurrentFilePath)
                ? loader.CurrentFilePath
                : (loader != null ? loader.GetFilePath() : string.Empty);
            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
            {
                PointCloudProgressManager.Instance.ShowError("ノイズ解析", "点群ファイルが読み込まれていないか、パスが無効です。");
                return;
            }

            string outputDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/output"));
            PointData[] points = editor.targetRenderer.GetPointData();
            PointCloudRenderer rendererSnapshot = editor.targetRenderer;
            long datasetGeneration = rendererSnapshot.DatasetGeneration;
            long contentRevision = rendererSnapshot.ContentRevision;
            float coordinateScaleToMm = editor.targetRenderer.DisplayScale;
            NoiseFilterParams parameterSnapshot = JsonUtility.FromJson<NoiseFilterParams>(JsonUtility.ToJson(Params));
            PointCloudProgressManager pm = PointCloudProgressManager.Instance;
            PointCloudOperation operation = pm.TryStart("空中モヤ・浮遊点ノイズ除去", "Pythonプロセスを準備中...");
            if (operation == null) return;
            activeOperation = operation;
            CancellationToken token = operation.CancellationToken;

            try
            {
                NoiseFilterResult result = await PythonBridge.RunDenoiserAsync(
                    inputPath, outputDir, parameterSnapshot, points, coordinateScaleToMm, operation, token);
                token.ThrowIfCancellationRequested();
                if (editor == null || editor.targetRenderer != rendererSnapshot ||
                    !NoiseFilterManager.Instance.SetResult(result, rendererSnapshot, datasetGeneration, contentRevision) ||
                    !NoiseFilterManager.Instance.ApplyPreview(rendererSnapshot))
                    throw new InvalidOperationException("点群または編集状態が解析中に変化しました。古い結果は適用していません。");
                editor.MarkStatsDirty();
                operation.Complete();
            }
            catch (OperationCanceledException)
            {
                operation.CompleteCancelled("ノイズ解析をキャンセルしました。点群の選択状態は変更していません。");
            }
            catch (Exception ex)
            {
                operation.Fail("空中モヤ・浮遊点ノイズ除去", "Python解析に失敗しました。点群の編集内容は適用していません。", ex.ToString());
                UnityEngine.Debug.LogWarning($"[RecoverableOperationError] ノイズ解析: {ex}");
            }
            finally
            {
                if (ReferenceEquals(activeOperation, operation)) activeOperation = null;
            }
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

        public void DrawPreviewLegendContents()
        {
            NoiseFilterManager manager = NoiseFilterManager.Instance;
            if (manager == null || !manager.IsPreviewActive)
            {
                return;
            }

            InitializeLegendStyles();
            GUILayout.Label("除去対象ノイズ凡例 (プレビュー)", legendTitleStyle);
            GUILayout.Space(8);

            DrawLegendItem(new Color(0.0f, 0.85f, 1.0f, 1.0f), $"空中白モヤ (White Haze)：水色 ({manager.GetPreviewReasonCount(7):N0} 点)");
            DrawLegendItem(new Color(1.0f, 0.12f, 0.12f, 1.0f), $"SOR (統計的ノイズ除去)：赤 ({manager.GetPreviewReasonCount(1):N0} 点)");
            DrawLegendItem(new Color(1.0f, 0.55f, 0.0f, 1.0f), $"ROR (半径外れ値除去)：橙 ({manager.GetPreviewReasonCount(2):N0} 点)");
            DrawLegendItem(new Color(0.63f, 0.12f, 0.9f, 1.0f), $"低密度ノイズ除去：紫 ({manager.GetPreviewReasonCount(3):N0} 点)");
            DrawLegendItem(new Color(1.0f, 0.86f, 0.0f, 1.0f), $"クラスタノイズ (DBSCAN)：黄 ({manager.GetPreviewReasonCount(4):N0} 点)");
            DrawLegendItem(new Color(1.0f, 0.0f, 0.5f, 1.0f), $"平面推定 (CC風)ノイズ：ピンク ({manager.GetPreviewReasonCount(5):N0} 点)");
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
            activeOperation?.Cancel();
            if (legendBgTexture != null) Destroy(legendBgTexture);
            if (colorTexture != null) Destroy(colorTexture);
        }
    }
}
