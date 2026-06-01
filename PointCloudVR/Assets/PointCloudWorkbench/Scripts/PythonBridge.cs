using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PointCloudWorkbench
{
    /// <summary>
    /// PythonバックエンドからロードされるJSONメタデータの簡易パース用クラス。
    /// </summary>
    [System.Serializable]
    public class NoiseFilterMetadata
    {
        public int point_count;
        public string mode;
        public string dbscan_mode;
        public float dbscan_voxel_size;
        public int dbscan_analysis_count;
        public float voxel_size;
    }

    /// <summary>
    /// Pythonバックエンドプログラム（run_noise_filter.py）とUnity/C#間の通信・非同期実行を担うクラス。
    /// </summary>
    public static class PythonBridge
    {
        /// <summary>
        /// 実行環境における仮想環境の python.exe のパスを取得します。
        /// 存在しない場合はシステム環境パスの python.exe を探します。
        /// </summary>
        private static string GetPythonPath()
        {
            string venvPath = Path.Combine(Application.dataPath, "../python_backend/.venv/Scripts/python.exe");
            if (File.Exists(venvPath))
            {
                return Path.GetFullPath(venvPath);
            }
            return "python";
        }

        /// <summary>
        /// 実行する Python スクリプトのフルパスを取得します。
        /// </summary>
        private static string GetScriptPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/run_noise_filter.py"));
        }

        /// <summary>
        /// バックグラウンドプロセスでノイズ除去スクリプトを非同期実行し、完了後に結果オブジェクトを返します。
        /// </summary>
        /// <param name="inputPlyPath">入力PLY点群のパス</param>
        /// <param name="outputDir">バイナリファイルの出力先ディレクトリ</param>
        /// <param name="filterParams">統合ノイズフィルタパラメータ</param>
        /// <param name="cancellationToken">キャンセル監視トークン</param>
        public static async Task<NoiseFilterResult> RunDenoiserAsync(
            string inputPlyPath, 
            string outputDir, 
            NoiseFilterParams filterParams,
            CancellationToken cancellationToken = default)
        {
            string pythonPath = GetPythonPath();
            string scriptPath = GetScriptPath();

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Pythonノイズフィルタスクリプトが見つかりません: {scriptPath}");
            }

            // 出力ディレクトリの作成
            Directory.CreateDirectory(outputDir);

            // 引数の組み立て
            string arguments = BuildArguments(scriptPath, inputPlyPath, outputDir, filterParams);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };


            UnityEngine.Debug.Log($"[PythonBridge] 実行コマンド: {pythonPath} {psi.Arguments}");

            using (Process process = new Process())
            {
                process.StartInfo = psi;

                StringBuilder outputLog = new StringBuilder();
                StringBuilder errorLog = new StringBuilder();

                // 最後のログ通信日時（初期値はUTCの現在のTick数）
                long lastActivityTicks = System.DateTime.UtcNow.Ticks;

                // 出力を非同期で読み出し、進捗ステータスを更新する
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        outputLog.AppendLine(e.Data);
                        UnityEngine.Debug.Log($"[Python Out] {e.Data}");
                        
                        // 進捗更新メッセージの簡易パーサー
                        if (e.Data.Contains("PLYファイルをロード中"))
                        {
                            PointCloudProgressManager.Instance.Update(0.2f, "点群データをPythonへロード中...");
                        }
                        else if (e.Data.Contains("処理を開始します"))
                        {
                            PointCloudProgressManager.Instance.Update(0.4f, "ノイズ除去アルゴリズムを実行中 (SOR/ROR/DBSCAN)...");
                        }
                        else if (e.Data.Contains("自動ダウンサンプリング"))
                        {
                            PointCloudProgressManager.Instance.Update(0.6f, "DBSCANクラスタ検出を自動ダウンサンプルして実行中...");
                        }
                        else if (e.Data.Contains("結果出力ディレクトリ"))
                        {
                            PointCloudProgressManager.Instance.Update(0.8f, "結果バイナリデータを保存中...");
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        errorLog.AppendLine(e.Data);
                        UnityEngine.Debug.LogError($"[Python Err] {e.Data}");
                    }
                };

                if (!process.Start())
                {
                    throw new Exception("Pythonプロセスの開始に失敗しました。");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // プロセス終了を非同期で監視し、キャンセル・無通信タイムアウトも検知
                const int timeoutSeconds = 180; // 3分無通信タイムアウト

                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            process.Kill();
                            UnityEngine.Debug.LogWarning("[PythonBridge] ユーザーのキャンセル要求により、Pythonプロセスを強制終了しました。");
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogError($"[PythonBridge] プロセス強制終了エラー: {ex.Message}");
                        }
                        throw new OperationCanceledException(cancellationToken);
                    }

                    // 無通信タイムアウト検知（最後のログから180秒経過）
                    long lastTicks = System.Threading.Interlocked.Read(ref lastActivityTicks);
                    double idleSeconds = (System.DateTime.UtcNow.Ticks - lastTicks) / (double)System.TimeSpan.TicksPerSecond;

                    if (idleSeconds > timeoutSeconds)
                    {
                        try
                        {
                            process.Kill();
                            UnityEngine.Debug.LogError($"[PythonBridge] Pythonプロセス�        /// <summary>
        /// NoiseFilterParams オブジェクトから Python スクリプト実行用のコマンドライン引数を構築します。
        /// 同時に、順序と個別パラメータを含んだ JSON 構成ファイルを保存し、引数で渡します。
        /// </summary>
        private static string BuildArguments(string scriptPath, string inputPlyPath, string outputDir, NoiseFilterParams p)
        {
            // パイプライン構成JSONの構築
            var pipelineSteps = p.GetPipeline();
            var jsonBuilder = new StringBuilder();
            jsonBuilder.Append("{\n");
            jsonBuilder.Append($"  \"processMode\": \"{p.processMode}\",\n");
            jsonBuilder.Append($"  \"voxelSize\": {p.voxelSize.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
            jsonBuilder.Append("  \"steps\": [\n");

            for (int i = 0; i < pipelineSteps.Count; i++)
            {
                var step = pipelineSteps[i];
                jsonBuilder.Append("    {\n");
                jsonBuilder.Append($"      \"name\": \"{step.name}\",\n");
                jsonBuilder.Append($"      \"enabled\": {(step.enabled ? "true" : "false")},\n");
                jsonBuilder.Append($"      \"excludeFromNext\": {(step.excludeFromNext ? "true" : "false")},\n");
                jsonBuilder.Append("      \"params\": {\n");

                // 各設定の個別パラメータをシリアライズ
                if (step is WhiteHazeConfig wh)
                {
                    jsonBuilder.Append($"        \"brightness_min\": {wh.brightness.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    jsonBuilder.Append($"        \"saturation_max\": {wh.saturation.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
                }
                else if (step is CcConfig cc)
                {
                    jsonBuilder.Append($"        \"use_knn\": {(cc.useKnn ? "true" : "false")},\n");
                    jsonBuilder.Append($"        \"k\": {cc.k},\n");
                    jsonBuilder.Append($"        \"radius\": {cc.radius.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    jsonBuilder.Append($"        \"remove_isolated_points\": {(cc.removeIsolated ? "true" : "false")},\n");
                    jsonBuilder.Append($"        \"use_relative\": {(cc.useRelative ? "true" : "false")},\n");
                    jsonBuilder.Append($"        \"relative_sigma\": {cc.sigma.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    jsonBuilder.Append($"        \"absolute_error\": {cc.error.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
                }
                else if (step is SorConfig sor)
                {
                    jsonBuilder.Append($"        \"nb_neighbors\": {sor.nb},\n");
                    jsonBuilder.Append($"        \"std_ratio\": {sor.std.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
                }
                else if (step is RorConfig ror)
                {
                    jsonBuilder.Append($"        \"radius_multiplier\": {ror.mul.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    jsonBuilder.Append($"        \"min_neighbors\": {ror.min}\n");
                }
                else if (step is DensityConfig dn)
                {
                    jsonBuilder.Append($"        \"k\": {dn.k},\n");
                    jsonBuilder.Append($"        \"threshold\": {dn.threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
                }
                else if (step is DbscanConfig db)
                {
                    jsonBuilder.Append($"        \"eps_multiplier\": {db.eps.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    jsonBuilder.Append($"        \"min_points\": {db.min},\n");
                    jsonBuilder.Append($"        \"min_cluster_size\": {db.cluster},\n");
                    jsonBuilder.Append($"        \"target_points\": {db.target},\n");
                    jsonBuilder.Append($"        \"timeout_sec\": {db.timeout}\n");
                }
                else
                {
                    // フォールバック（パラメータなし）
                    jsonBuilder.Append("        \"_dummy\": 0\n");
                }

                jsonBuilder.Append("      }\n");
                jsonBuilder.Append(i < pipelineSteps.Count - 1 ? "    },\n" : "    }\n");
            }
            jsonBuilder.Append("  ]\n");
            jsonBuilder.Append("}");

            // JSONファイルの書き出し
            string configJsonPath = Path.Combine(outputDir, "pipeline_config.json");
            try
            {
                File.WriteAllText(configJsonPath, jsonBuilder.ToString());
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PythonBridge] パイプライン構成JSONの書き込みに失敗しました: {ex.Message}");
            }

            StringBuilder argsBuilder = new StringBuilder();
            argsBuilder.Append("-u "); // Pythonの出力をバッファリングせずリアルタイムに出力させる
            argsBuilder.Append($"\"{scriptPath}\"");
            argsBuilder.Append($" --input \"{inputPlyPath}\"");
            argsBuilder.Append($" --output_dir \"{outputDir}\"");
            argsBuilder.Append($" --config_json \"{configJsonPath}\"");

            return argsBuilder.ToString();
        }Builder.Append($" --ror_min {p.ror.min}");
            argsBuilder.Append($" --density_k {p.density.k}");
            argsBuilder.Append($" --density_thresh {p.density.threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            argsBuilder.Append($" --cc_k {p.cc.k}");
            argsBuilder.Append($" --cc_sigma {p.cc.sigma.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            argsBuilder.Append($" --cc_error {p.cc.error.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            argsBuilder.Append($" --cc_use_knn {(p.cc.useKnn ? "true" : "false")}");
            argsBuilder.Append($" --cc_radius {p.cc.radius.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            argsBuilder.Append($" --cc_remove_isolated {(p.cc.removeIsolated ? "true" : "false")}");
            argsBuilder.Append($" --cc_use_relative {(p.cc.useRelative ? "true" : "false")}");
            argsBuilder.Append($" --dbscan_eps {p.dbscan.eps.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            argsBuilder.Append($" --dbscan_min {p.dbscan.min}");
            argsBuilder.Append($" --dbscan_cluster {p.dbscan.cluster}");
            argsBuilder.Append($" --dbscan_target {p.dbscan.target}");
            argsBuilder.Append($" --wh_brightness {p.whiteHaze.brightness.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            argsBuilder.Append($" --wh_saturation {p.whiteHaze.saturation.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            return argsBuilder.ToString();
        }


        /// <summary>
        /// 指定された出力ディレクトリのバイナリファイルおよびJSONを高速ロードします。
        /// </summary>
        private static NoiseFilterResult LoadFilterResult(string outputDir)
        {
            string metadataPath = Path.Combine(outputDir, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                throw new FileNotFoundException($"メタデータ JSON ファイルが見つかりません: {metadataPath}");
            }

            string jsonContent = File.ReadAllText(metadataPath);
            NoiseFilterMetadata meta = JsonUtility.FromJson<NoiseFilterMetadata>(jsonContent);
            int count = meta.point_count;

            // 各種バイナリファイルを高速ロード
            byte[] previewMask = LoadBinaryBytes(Path.Combine(outputDir, "preview_mask.bin"), count);
            byte[] whiteHazeCandidateMask = LoadBinaryBytes(Path.Combine(outputDir, "white_haze_candidate_mask.bin"), count);
            byte[] removeMask = LoadBinaryBytes(Path.Combine(outputDir, "remove_mask.bin"), count);
            float[] sorScore = LoadBinaryFloats(Path.Combine(outputDir, "sor_score.bin"), count);
            float[] densityScore = LoadBinaryFloats(Path.Combine(outputDir, "density_score.bin"), count);
            int[] radiusNeighbor = LoadBinaryInts(Path.Combine(outputDir, "radius_neighbor_count.bin"), count);
            float[] ccNoiseScore = LoadBinaryFloats(Path.Combine(outputDir, "cc_noise_score.bin"), count);
            float[] whiteHazeScore = LoadBinaryFloats(Path.Combine(outputDir, "white_haze_score.bin"), count);
            int[] clusterId = LoadBinaryInts(Path.Combine(outputDir, "cluster_id.bin"), count);
            int[] previewReason = LoadBinaryInts(Path.Combine(outputDir, "preview_reason.bin"), count);
            int[] reason = LoadBinaryInts(Path.Combine(outputDir, "reason.bin"), count);

            return new NoiseFilterResult(count, previewMask, whiteHazeCandidateMask, removeMask, sorScore, densityScore, radiusNeighbor, ccNoiseScore, whiteHazeScore, clusterId, previewReason, reason);
        }

        private static byte[] LoadBinaryBytes(string path, int expectedCount)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"バイナリファイルが見つかりません: {path}");
            }
            byte[] data = File.ReadAllBytes(path);
            if (data.Length != expectedCount)
            {
                throw new Exception($"バイナリサイズが期待される点数と不一致です: {path} (期待値: {expectedCount} bytes, 実際: {data.Length} bytes)");
            }
            return data;
        }

        private static float[] LoadBinaryFloats(string path, int count)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"バイナリファイルが見つかりません: {path}");
            }
            byte[] rawBytes = File.ReadAllBytes(path);
            if (rawBytes.Length != count * sizeof(float))
            {
                throw new Exception($"バイナリサイズが期待されるサイズと不一致です: {path} (期待値: {count * sizeof(float)} bytes, 実際: {rawBytes.Length} bytes)");
            }

            float[] data = new float[count];
            Buffer.BlockCopy(rawBytes, 0, data, 0, rawBytes.Length);
            return data;
        }

        private static int[] LoadBinaryInts(string path, int count)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"バイナリファイルが見つかりません: {path}");
            }
            byte[] rawBytes = File.ReadAllBytes(path);
            if (rawBytes.Length != count * sizeof(int))
            {
                throw new Exception($"バイナリサイズが期待されるサイズと不一致です: {path} (期待値: {count * sizeof(int)} bytes, 実際: {rawBytes.Length} bytes)");
            }

            int[] data = new int[count];
            Buffer.BlockCopy(rawBytes, 0, data, 0, rawBytes.Length);
            return data;
        }
    }
}
