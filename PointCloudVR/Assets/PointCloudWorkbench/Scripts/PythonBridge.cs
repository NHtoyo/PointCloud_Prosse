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
    /// Python繝舌ャ繧ｯ繧ｨ繝ｳ繝峨°繧峨Ο繝ｼ繝峨＆繧後ｋJSON繝｡繧ｿ繝��繧ｿ縺ｮ邁｡譏薙ヱ繝ｼ繧ｹ逕ｨ繧ｯ繝ｩ繧ｹ縲�
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
    /// Python繝舌ャ繧ｯ繧ｨ繝ｳ繝峨�繝ｭ繧ｰ繝ｩ繝���un_noise_filter.py�峨→Unity/C#髢薙�騾壻ｿ｡繝ｻ髱槫酔譛溷ｮ溯｡後ｒ諡�≧繧ｯ繝ｩ繧ｹ縲�
    /// </summary>
    public static class PythonBridge
    {
        /// <summary>
        /// 螳溯｡檎腸蠅�↓縺翫￠繧倶ｻｮ諠ｳ迺ｰ蠅�� python.exe 縺ｮ繝代せ繧貞叙蠕励＠縺ｾ縺吶�
        /// 蟄伜惠縺励↑縺��ｴ蜷医�繧ｷ繧ｹ繝�Β迺ｰ蠅�ヱ繧ｹ縺ｮ python.exe 繧呈爾縺励∪縺吶�
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
        /// 螳溯｡後☆繧 Python 繧ｹ繧ｯ繝ｪ繝励ヨ縺ｮ繝輔Ν繝代せ繧貞叙蠕励＠縺ｾ縺吶€
        /// </summary>
        private static string GetScriptPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/run_noise_filter.py"));
        }

        private static string GetScaleCalibScriptPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/1_scale_calibration.py"));
        }

        private static string GetDownsampleScriptPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/2_downsample.py"));
        }

        /// <summary>
        /// 繝舌ャ繧ｯ繧ｰ繝ｩ繧ｦ繝ｳ繝峨繝ｭ繧ｻ繧ｹ縺ｧ繝弱う繧ｺ髯､蜴ｻ繧ｹ繧ｯ繝ｪ繝励ヨ繧帝撼蜷梧悄螳溯｡後＠縲∝ｮ御ｺｾ後↓邨先棡繧ｪ繝悶ず繧ｧ繧ｯ繝医ｒ霑斐＠縺ｾ縺吶€
        /// </summary>
        /// <param name="inputPlyPath">蜈･蜉娜LY轤ｹ鄒､縺ｮ繝代せ</param>
        /// <param name="outputDir">繝舌う繝翫Μ繝輔ぃ繧､繝ｫ縺ｮ蜃ｺ蜉帛繝ぅ繝ｬ繧ｯ繝医Μ</param>
        /// <param name="filterParams">邨ｱ蜷医ヮ繧､繧ｺ繝輔ぅ繝ｫ繧ｿ繝代Λ繝｡繝ｼ繧ｿ</param>
        /// <param name="cancellationToken">繧ｭ繝｣繝ｳ繧ｻ繝ｫ逶｣隕悶ヨ繝ｼ繧ｯ繝ｳ</param>
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
                throw new FileNotFoundException($"Python繝弱う繧ｺ繝輔ぅ繝ｫ繧ｿ繧ｹ繧ｯ繝ｪ繝励ヨ縺瑚ｦ九▽縺九ｊ縺ｾ縺帙ｓ: {scriptPath}");
            }

            // 出力ディレクトリの作成
            Directory.CreateDirectory(outputDir);

            // 引数の構築
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

                // 最後の活動時刻（処理時間はUTCの現在時刻のTick数）
                long lastActivityTicks = System.DateTime.UtcNow.Ticks;

                // 出力を非同期で読み出し、進捗ステータスを更新する
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        outputLog.AppendLine(e.Data);
                        UnityEngine.Debug.Log($"[Python Out] {e.Data}");
                        
                        // 進捗更新メッセージのデシリアライズ/パーサー
                        if (e.Data.StartsWith("[Progress]"))
                        {
                            string partsStr = e.Data.Substring(10).Trim();
                            int spaceIndex = partsStr.IndexOf(' ');
                            if (spaceIndex > 0)
                            {
                                string numStr = partsStr.Substring(0, spaceIndex);
                                string msgStr = partsStr.Substring(spaceIndex + 1);
                                if (float.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float pct))
                                {
                                    // Python側の0〜100%の進捗を、C#全体の0.2f〜0.8f（20%〜80%）にマッピング
                                    float mappedProgress = 0.2f + 0.6f * (pct / 100f);
                                    PointCloudProgressManager.Instance.Update(mappedProgress, msgStr);
                                }
                            }
                        }
                        else if (e.Data.Contains("PLYファイルをロード中") || e.Data.Contains("PLY繝輔ぃ繧､繝ｫ繧偵Ο繝ｼ繝我ｸｭ"))
                        {
                            PointCloudProgressManager.Instance.Update(0.1f, "点群データをPythonへロード中...");
                        }
                        else if (e.Data.Contains("処理を開始します") || e.Data.Contains("蜃ｦ逅ｒ髢句ｧ九＠縺ｾ縺"))
                        {
                            PointCloudProgressManager.Instance.Update(0.2f, "ノイズ除去処理を開始します...");
                        }
                        else if (e.Data.Contains("結果出力ディレクトリ") || e.Data.Contains("邨先棡蜃ｺ蜉帙ョ繧｣繝ｬ繧ｯ繝医Μ"))
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
                    throw new Exception("Python繝励Ο繧ｻ繧ｹ縺ｮ髢句ｧ九↓螟ｱ謨励＠縺ｾ縺励◆縲");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 繝励Ο繧ｻ繧ｹ邨ゆｺｒ髱槫酔譛溘〒逶｣隕悶＠縲√く繝｣繝ｳ繧ｻ繝ｫ繝ｻ辟｡騾壻ｿ｡繧ｿ繧､繝繧｢繧ｦ繝医ｂ讀懃衍
                const int timeoutSeconds = 180; // 3蛻┌騾壻ｿ｡繧ｿ繧､繝繧｢繧ｦ繝

                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            process.Kill();
                            UnityEngine.Debug.LogWarning("[PythonBridge] 繝ｦ繝ｼ繧ｶ繝ｼ縺ｮ繧ｭ繝｣繝ｳ繧ｻ繝ｫ隕∵ｱゅ↓繧医ｊ縲￣ython繝励Ο繧ｻ繧ｹ繧貞ｼｷ蛻ｶ邨ゆｺ＠縺ｾ縺励◆縲");
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogError($"[PythonBridge] 繝励Ο繧ｻ繧ｹ蠑ｷ蛻ｶ邨ゆｺお繝ｩ繝ｼ: {ex.Message}");
                        }
                        throw new OperationCanceledException(cancellationToken);
                    }

                    // 辟｡騾壻ｿ｡繧ｿ繧､繝繧｢繧ｦ繝域､懃衍域怙蠕後繝ｭ繧ｰ縺九ｉ180遘堤ｵ碁℃
                    long lastTicks = System.Threading.Interlocked.Read(ref lastActivityTicks);
                    double idleSeconds = (System.DateTime.UtcNow.Ticks - lastTicks) / (double)System.TimeSpan.TicksPerSecond;

                    if (idleSeconds > timeoutSeconds)
                    {
                        try
                        {
                            process.Kill();
                            UnityEngine.Debug.LogError($"[PythonBridge] Pythonプロセスが {timeoutSeconds}秒 間応答しなかった（ログが出力されなかった）ため、強制終了しました。");
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogError($"[PythonBridge] タイムアウト強制終了エラー: {ex.Message}");
                        }
                        throw new TimeoutException($"Pythonノイズフィルタの処理が {timeoutSeconds}秒 間ログを出力せず応答しなかったため、タイムアウトしました。\n[出力ログ]\n{outputLog.ToString()}\n[エラーログ]\n{errorLog.ToString()}");
                    }

                    // 100msウェイト
                    await Task.Delay(100);
                }

                if (process.ExitCode != 0)
                {
                    string errText = errorLog.ToString();
                    string outText = outputLog.ToString();
                    throw new Exception($"Pythonノイズフィルタがエラーで終了しました (ExitCode: {process.ExitCode})\n[エラーログ]\n{errText}\n[出力ログ]\n{outText}");
                }
            }

            // 終了後にバイナリファイルを読み込み
            PointCloudProgressManager.Instance.Update(0.9f, "バイナリ結果データをロード中...");
            return LoadFilterResult(outputDir);
        }

        /// <summary>
        /// スケール校正スクリプトを非同期実行します。
        /// </summary>
        public static async Task<bool> RunScaleCalibrationAsync(
            float realDiameter,
            string measurements,
            string outputJsonPath = "config/scale_calibration_report.json",
            CancellationToken cancellationToken = default)
        {
            string pythonPath = GetPythonPath();
            string scriptPath = GetScaleCalibScriptPath();

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"スケール校正スクリプトが見つかりません: {scriptPath}");
            }

            // 引数の構築
            string arguments = $"-u \"{scriptPath}\" --real_diameter {realDiameter.ToString(System.Globalization.CultureInfo.InvariantCulture)} --measurements \"{measurements}\" --output \"{outputJsonPath}\"";

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

            UnityEngine.Debug.Log($"[PythonBridge] スケール校正実行コマンド: {pythonPath} {psi.Arguments}");
            PointCloudProgressManager.Instance.Update(0.1f, "スケール校正処理を開始中...");

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                StringBuilder outputLog = new StringBuilder();
                StringBuilder errorLog = new StringBuilder();

                long lastActivityTicks = System.DateTime.UtcNow.Ticks;
                const int timeoutSeconds = 60; // 校正は短いので60秒

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        outputLog.AppendLine(e.Data);
                        UnityEngine.Debug.Log($"[Scale Calib Out] {e.Data}");
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        errorLog.AppendLine(e.Data);
                        UnityEngine.Debug.LogError($"[Scale Calib Err] {e.Data}");
                    }
                };

                if (!process.Start())
                {
                    throw new Exception("スケール校正プロセスの開始に失敗しました。");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        throw new OperationCanceledException(cancellationToken);
                    }

                    long lastTicks = System.Threading.Interlocked.Read(ref lastActivityTicks);
                    double idleSeconds = (System.DateTime.UtcNow.Ticks - lastTicks) / (double)System.TimeSpan.TicksPerSecond;
                    if (idleSeconds > timeoutSeconds)
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("スケール校正処理がタイムアウトしました。");
                    }

                    await Task.Delay(100);
                }

                if (process.ExitCode != 0)
                {
                    throw new Exception($"スケール校正がエラーで終了しました (ExitCode: {process.ExitCode})\n{errorLog.ToString()}");
                }
            }

            PointCloudProgressManager.Instance.Update(1.0f, "スケール校正が完了しました。");
            return true;
        }

        /// <summary>
        /// ダウンサンプリングスクリプトを非同期実行します。
        /// </summary>
        public static async Task<bool> RunDownsamplingAsync(
            string inputDir,
            string outputDir,
            string scaleJson = "config/scale_calibration_report.json",
            int mode = 1,
            float voxelSize = 5.0f,
            CancellationToken cancellationToken = default)
        {
            string pythonPath = GetPythonPath();
            string scriptPath = GetDownsampleScriptPath();

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"ダウンサンプリングスクリプトが見つかりません: {scriptPath}");
            }

            // 引数の構築
            string arguments = $"-u \"{scriptPath}\" --input \"{inputDir}\" --output \"{outputDir}\" --scale_json \"{scaleJson}\" --mode {mode} --voxel_size {voxelSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

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

            UnityEngine.Debug.Log($"[PythonBridge] ダウンサンプリング実行コマンド: {pythonPath} {psi.Arguments}");
            PointCloudProgressManager.Instance.Update(0.05f, "ダウンサンプリング処理を開始中...");

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                StringBuilder outputLog = new StringBuilder();
                StringBuilder errorLog = new StringBuilder();

                long lastActivityTicks = System.DateTime.UtcNow.Ticks;
                const int timeoutSeconds = 300; // 5分

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        outputLog.AppendLine(e.Data);
                        UnityEngine.Debug.Log($"[Downsample Out] {e.Data}");

                        if (e.Data.StartsWith("[Progress]"))
                        {
                            string partsStr = e.Data.Substring(10).Trim();
                            int spaceIndex = partsStr.IndexOf(' ');
                            if (spaceIndex > 0)
                            {
                                string numStr = partsStr.Substring(0, spaceIndex);
                                string msgStr = partsStr.Substring(spaceIndex + 1);
                                if (float.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float pct))
                                {
                                    float mappedProgress = pct / 100f;
                                    PointCloudProgressManager.Instance.Update(mappedProgress, msgStr);
                                }
                            }
                            else
                            {
                                if (float.TryParse(partsStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float pct))
                                {
                                    float mappedProgress = pct / 100f;
                                    PointCloudProgressManager.Instance.Update(mappedProgress, "実行中...");
                                }
                            }
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        errorLog.AppendLine(e.Data);
                        UnityEngine.Debug.LogError($"[Downsample Err] {e.Data}");
                    }
                };

                if (!process.Start())
                {
                    throw new Exception("ダウンサンプリングプロセスの開始に失敗しました。");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        throw new OperationCanceledException(cancellationToken);
                    }

                    long lastTicks = System.Threading.Interlocked.Read(ref lastActivityTicks);
                    double idleSeconds = (System.DateTime.UtcNow.Ticks - lastTicks) / (double)System.TimeSpan.TicksPerSecond;
                    if (idleSeconds > timeoutSeconds)
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("ダウンサンプリング処理がタイムアウトしました。");
                    }

                    await Task.Delay(100);
                }

                if (process.ExitCode != 0)
                {
                    throw new Exception($"ダウンサンプリングがエラーで終了しました (ExitCode: {process.ExitCode})\n{errorLog.ToString()}");
                }
            }

            PointCloudProgressManager.Instance.Update(1.0f, "ダウンサンプリングが完了しました。");
            return true;
        }

        /// <summary>
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
                    jsonBuilder.Append($"        \"threshold\": {dn.threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    jsonBuilder.Append($"        \"percentile\": {dn.percentile.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
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
        }

        /// <summary>
        /// 謖�ｮ壹＆繧後◆蜃ｺ蜉帙ョ繧｣繝ｬ繧ｯ繝医Μ縺ｮ繝舌う繝翫Μ繝輔ぃ繧､繝ｫ縺翫ｈ縺ｳJSON繧帝ｫ倬溘Ο繝ｼ繝峨＠縺ｾ縺吶�
        /// </summary>
        private static NoiseFilterResult LoadFilterResult(string outputDir)
        {
            string metadataPath = Path.Combine(outputDir, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                throw new FileNotFoundException($"繝｡繧ｿ繝��繧ｿ JSON 繝輔ぃ繧､繝ｫ縺瑚ｦ九▽縺九ｊ縺ｾ縺帙ｓ: {metadataPath}");
            }

            string jsonContent = File.ReadAllText(metadataPath);
            NoiseFilterMetadata meta = JsonUtility.FromJson<NoiseFilterMetadata>(jsonContent);
            int count = meta.point_count;

            // 蜷�ｨｮ繝舌う繝翫Μ繝輔ぃ繧､繝ｫ繧帝ｫ倬溘Ο繝ｼ繝�
            byte[] previewMask = LoadBinaryBytes(Path.Combine(outputDir, "preview_mask.bin"), count);
            byte[] whiteHazeCandidateMask = LoadBinaryBytes(Path.Combine(outputDir, "white_haze_candidate_mask.bin"), count);
            byte[] removeMask = LoadBinaryBytes(Path.Combine(outputDir, "remove_mask.bin"), count);
            float[] sorScore = LoadBinaryFloatsOptional(Path.Combine(outputDir, "sor_score.bin"), count);
            float[] densityScore = LoadBinaryFloatsOptional(Path.Combine(outputDir, "density_score.bin"), count);
            int[] radiusNeighbor = LoadBinaryIntsOptional(Path.Combine(outputDir, "radius_neighbor_count.bin"), count, 0);
            float[] ccNoiseScore = LoadBinaryFloatsOptional(Path.Combine(outputDir, "cc_noise_score.bin"), count);
            float[] whiteHazeScore = LoadBinaryFloatsOptional(Path.Combine(outputDir, "white_haze_score.bin"), count);
            int[] clusterId = LoadBinaryIntsOptional(Path.Combine(outputDir, "cluster_id.bin"), count, -1);
            int[] previewReason = LoadBinaryInts(Path.Combine(outputDir, "preview_reason.bin"), count);
            int[] reason = LoadBinaryIntsOptional(Path.Combine(outputDir, "reason.bin"), count, 0);

            return new NoiseFilterResult(count, previewMask, whiteHazeCandidateMask, removeMask, sorScore, densityScore, radiusNeighbor, ccNoiseScore, whiteHazeScore, clusterId, previewReason, reason);
        }

        private static byte[] LoadBinaryBytes(string path, int expectedCount)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"繝舌う繝翫Μ繝輔ぃ繧､繝ｫ縺瑚ｦ九▽縺九ｊ縺ｾ縺帙ｓ: {path}");
            }
            byte[] data = File.ReadAllBytes(path);
            if (data.Length != expectedCount)
            {
                throw new Exception($"繝舌う繝翫Μ繧ｵ繧､繧ｺ縺梧悄蠕�＆繧後ｋ轤ｹ謨ｰ縺ｨ荳堺ｸ閾ｴ縺ｧ縺�: {path} (譛溷ｾ�､: {expectedCount} bytes, 螳滄圀: {data.Length} bytes)");
            }
            return data;
        }

        private static float[] LoadBinaryFloats(string path, int count)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"繝舌う繝翫Μ繝輔ぃ繧､繝ｫ縺瑚ｦ九▽縺九ｊ縺ｾ縺帙ｓ: {path}");
            }
            byte[] rawBytes = File.ReadAllBytes(path);
            if (rawBytes.Length != count * sizeof(float))
            {
                throw new Exception($"繝舌う繝翫Μ繧ｵ繧､繧ｺ縺梧悄蠕�＆繧後ｋ繧ｵ繧､繧ｺ縺ｨ荳堺ｸ閾ｴ縺ｧ縺�: {path} (譛溷ｾ�､: {count * sizeof(float)} bytes, 螳滄圀: {rawBytes.Length} bytes)");
            }

            float[] data = new float[count];
            Buffer.BlockCopy(rawBytes, 0, data, 0, rawBytes.Length);
            return data;
        }

        private static float[] LoadBinaryFloatsOptional(string path, int count)
        {
            if (!File.Exists(path))
            {
                return new float[count];
            }
            return LoadBinaryFloats(path, count);
        }

        private static int[] LoadBinaryInts(string path, int count)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"繝舌う繝翫Μ繝輔ぃ繧､繝ｫ縺瑚ｦ九▽縺九ｊ縺ｾ縺帙ｓ: {path}");
            }
            byte[] rawBytes = File.ReadAllBytes(path);
            if (rawBytes.Length != count * sizeof(int))
            {
                throw new Exception($"繝舌う繝翫Μ繧ｵ繧､繧ｺ縺梧悄蠕�＆繧後ｋ繧ｵ繧､繧ｺ縺ｨ荳堺ｸ閾ｴ縺ｧ縺�: {path} (譛溷ｾ�､: {count * sizeof(int)} bytes, 螳滄圀: {rawBytes.Length} bytes)");
            }

            int[] data = new int[count];
            Buffer.BlockCopy(rawBytes, 0, data, 0, rawBytes.Length);
            return data;
        }

        private static int[] LoadBinaryIntsOptional(string path, int count, int defaultValue)
        {
            if (!File.Exists(path))
            {
                int[] data = new int[count];
                if (defaultValue != 0)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = defaultValue;
                    }
                }
                return data;
            }
            return LoadBinaryInts(path, count);
        }
    }
}
