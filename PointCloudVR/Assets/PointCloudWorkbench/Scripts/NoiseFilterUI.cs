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

        // SOR (統計的ノイズ除去) パラメータ
        public bool runSor = true;
        public int sorNb = 20;
        public float sorStd = 1.5f;

        // ROR (半径外れ値除去) パラメータ
        public bool runRor = true;
        public float rorMul = 3.0f;
        public int rorMin = 8;

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
                PointCloudProgressManager.Instance.Complete();
            }
        }

        /// <summary>
        /// PointCloudEditorUI から呼び出され、 OnGUI のスクロールビュー内に項目を描画します。
        /// </summary>
        public void DrawNoiseFilterSection(float width, GUIStyle textStyle, GUIStyle buttonStyle, GUIStyle activeButtonStyle)
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
            if (GUILayout.Button("Downsample (プレビュー)", processMode == "downsample" ? activeButtonStyle : buttonStyle, GUILayout.Width((width - 35) / 2f)))
            {
                processMode = "downsample";
            }
            GUILayout.EndHorizontal();

            if (processMode == "downsample")
            {
                GUILayout.Label($"  ボクセルサイズ: {voxelSize:F4} m", textStyle);
                voxelSize = GUILayout.HorizontalSlider(voxelSize, 0.001f, 0.02f);
            }
            GUILayout.Space(5);

            // --- 2. SOR (統計的外れ値除去) ---
            runSor = GUILayout.Toggle(runSor, " 統計的ノイズ除去 (SOR) を有効化", textStyle);
            if (runSor)
            {
                GUILayout.Label($"    隣接点数 (Neighbors): {sorNb}", textStyle);
                sorNb = Mathf.RoundToInt(GUILayout.HorizontalSlider(sorNb, 5f, 50f));

                GUILayout.Label($"    標準偏差倍率 (StdMul): {sorStd:F2}", textStyle);
                sorStd = GUILayout.HorizontalSlider(sorStd, 0.5f, 3.0f);
            }
            GUILayout.Space(5);

            // --- 3. ROR (半径外れ値除去) ---
            runRor = GUILayout.Toggle(runRor, " 半径外れ値除去 (ROR) を有効化", textStyle);
            if (runRor)
            {
                GUILayout.Label($"    検索半径倍率 (RadiusMul): {rorMul:F2}", textStyle);
                rorMul = GUILayout.HorizontalSlider(rorMul, 1.0f, 10.0f);

                GUILayout.Label($"    最小隣接点数 (MinNeighbors): {rorMin}", textStyle);
                rorMin = Mathf.RoundToInt(GUILayout.HorizontalSlider(rorMin, 1f, 30f));
            }
            GUILayout.Space(5);

            // --- 4. DBSCAN (クラスタノイズ除去) ---
            runDbscan = GUILayout.Toggle(runDbscan, " クラスタノイズ除去 (DBSCAN) を有効化", textStyle);
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
                        runDbscan, dbscanEps, dbscanMin, dbscanCluster, dbscanTarget,
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
                    filterFailedFlag = true;
                }
            });
        }
    }
}
