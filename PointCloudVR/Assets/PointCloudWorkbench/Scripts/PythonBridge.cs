using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        public string operation_id;
        public int point_count;
        public string mode;
        public string dbscan_mode;
        public float dbscan_voxel_size;
        public int dbscan_analysis_count;
        public float voxel_size;
    }

    [Serializable]
    public sealed class ReferenceSphereOutput
    {
        public string method_name;
        public int input_point_count;
        public int component_point_count;
        public int component_removed_count;
        public int knn_k;
        public float connectivity_alpha;
        public float median_knn_distance;
        public float connectivity_epsilon;
        public int fit_inlier_count;
        public float[] center;
        public float radius;
        public float diameter;
        public float[] diameter_point1;
        public float[] diameter_point2;
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
        public static string GetPythonPath()
        {
            string userVenv = GetUserVenvPythonPath();
            if (File.Exists(userVenv)) return userVenv;
            string packagedVenv = GetPackagedVenvPythonPath();
            if (File.Exists(packagedVenv)) return packagedVenv;
            return "python";
        }

        private static string GetPackagedVenvPythonPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/.venv/Scripts/python.exe"));
        }

        private static string GetUserVenvDirectory()
        {
            return Path.Combine(Application.persistentDataPath, "PythonEnvironment", ".venv");
        }

        private static string GetUserVenvPythonPath()
        {
            return Path.Combine(GetUserVenvDirectory(), "Scripts", "python.exe");
        }

        /// <summary>
        /// 螳溯｡後☆繧 Python 繧ｹ繧ｯ繝ｪ繝励ヨ縺ｮ繝輔Ν繝代せ繧貞叙蠕励＠縺ｾ縺吶€
        /// </summary>
        private static string GetScriptPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/run_noise_filter.py"));
        }

        private static string GetDownsampleScriptPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/2_downsample.py"));
        }

        private static string GetReferenceSphereScriptPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/run_reference_sphere.py"));
        }

        private static string GetProjectRootPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static async Task TerminateAndWaitAsync(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException)
            {
                if (!process.HasExited) throw;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (!process.HasExited) throw;
            }

            await Task.Run(() => process.WaitForExit());
        }

        public static async Task<ReferenceSphereOutput> RunReferenceSphereAsync(
            string selectedPointPath,
            string outputJsonPath,
            int knnK,
            float connectivityAlpha,
            PointCloudOperation operation,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(selectedPointPath) || !File.Exists(selectedPointPath))
                throw new FileNotFoundException("選択点の一時PLYが見つかりません。", selectedPointPath);
            if (string.IsNullOrWhiteSpace(outputJsonPath))
                throw new ArgumentException("推定結果JSONの出力先がありません。", nameof(outputJsonPath));
            if (knnK < 1) throw new ArgumentOutOfRangeException(nameof(knnK));
            if (float.IsNaN(connectivityAlpha) || float.IsInfinity(connectivityAlpha) ||
                connectivityAlpha <= 0f || connectivityAlpha > 10f)
                throw new ArgumentOutOfRangeException(nameof(connectivityAlpha));

            await EnsureEnvironmentReadyAsync(cancellationToken, operation);
            cancellationToken.ThrowIfCancellationRequested();
            string pythonPath = GetPythonPath();
            string scriptPath = GetReferenceSphereScriptPath();
            if (!File.Exists(scriptPath)) throw new FileNotFoundException("球直径推定スクリプトが見つかりません。", scriptPath);

            string outputFullPath = Path.GetFullPath(outputJsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath));
            string arguments = "-u " + QuoteCommandLineArgument(scriptPath)
                + " --input " + QuoteCommandLineArgument(Path.GetFullPath(selectedPointPath))
                + " --output " + QuoteCommandLineArgument(outputFullPath)
                + " --knn-k " + knnK.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " --connectivity-alpha " + connectivityAlpha.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(scriptPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var stdout = new BoundedTextBuffer();
            var stderr = new BoundedTextBuffer();
            object logLock = new object();
            long lastActivityTicks = DateTime.UtcNow.Ticks;
            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    System.Threading.Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
                    lock (logLock) stdout.AppendLine(e.Data);
                    if (e.Data.StartsWith("[Progress]", StringComparison.Ordinal))
                    {
                        UnityEngine.Debug.Log("[ReferenceSphere] " + e.Data);
                        string progressText = e.Data.Substring(10).Trim();
                        int separator = progressText.IndexOf(' ');
                        if (separator > 0 && float.TryParse(progressText.Substring(0, separator),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float percent))
                        {
                            operation?.Update(0.15f + 0.72f * Mathf.Clamp01(percent / 100f),
                                progressText.Substring(separator + 1));
                        }
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    System.Threading.Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
                    lock (logLock) stderr.AppendLine(e.Data);
                    UnityEngine.Debug.LogWarning("[Python Error] " + e.Data);
                };

                UnityEngine.Debug.Log($"[ReferenceSphere] Python実行: {pythonPath} {arguments}");
                if (!process.Start()) throw new InvalidOperationException("Python推定プロセスを開始できませんでした。");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                const int timeoutSeconds = 180;
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await TerminateAndWaitAsync(process);
                        throw new OperationCanceledException(cancellationToken);
                    }
                    long activity = System.Threading.Interlocked.Read(ref lastActivityTicks);
                    double idleSeconds = (DateTime.UtcNow.Ticks - activity) / (double)TimeSpan.TicksPerSecond;
                    if (idleSeconds > timeoutSeconds)
                    {
                        await TerminateAndWaitAsync(process);
                        string outText;
                        string errText;
                        lock (logLock) { outText = stdout.ToString(); errText = stderr.ToString(); }
                        throw new TimeoutException($"球直径推定Pythonが{timeoutSeconds}秒間応答しませんでした。\n[stdout]\n{outText}\n[stderr]\n{errText}");
                    }
                    await Task.Delay(100);
                }
                await Task.Run(() => process.WaitForExit());
                if (process.ExitCode != 0)
                {
                    string outText;
                    string errText;
                    lock (logLock) { outText = stdout.ToString(); errText = stderr.ToString(); }
                    throw new InvalidOperationException($"球直径推定Pythonが失敗しました (ExitCode: {process.ExitCode})\n[stdout]\n{outText}\n[stderr]\n{errText}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(outputFullPath)) throw new InvalidDataException("Pythonが推定結果JSONを作成しませんでした。");
            string json = await Task.Run(() => File.ReadAllText(outputFullPath, Encoding.UTF8), cancellationToken);
            ReferenceSphereOutput result = JsonUtility.FromJson<ReferenceSphereOutput>(json);
            if (result == null || result.center == null || result.center.Length != 3 ||
                result.diameter_point1 == null || result.diameter_point1.Length != 3 ||
                result.diameter_point2 == null || result.diameter_point2.Length != 3)
                throw new InvalidDataException("推定結果JSONの形式が不正です。");
            return result;
        }

        private static string QuoteCommandLineArgument(string value)
        {
            var builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (current == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (current == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                }
                else
                {
                    builder.Append('\\', backslashes);
                    builder.Append(current);
                }
                backslashes = 0;
            }
            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        private static string ResolveProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A file path is required.", nameof(path));
            }

            string resolvedPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(GetProjectRootPath(), path);
            return Path.GetFullPath(resolvedPath);
        }

        /// <summary>
        /// Pythonの実行環境を確認し、必要ならプロジェクト内の.venvだけを構築します。
        /// </summary>
        public static async Task EnsureEnvironmentReadyAsync(CancellationToken cancellationToken, PointCloudOperation operation = null)
        {
            string packagedVenvPath = GetPackagedVenvPythonPath();
            string userVenvPath = GetUserVenvPythonPath();
            string pythonBackendDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend"));
            operation?.Update(0.01f, "Python環境を検証中...");

            if (File.Exists(userVenvPath))
            {
                bool ready = await CheckCommandExistsAsync(userVenvPath,
                    GetPythonValidationArguments(), cancellationToken);
                if (ready) return;
                operation?.Update(0.08f, "ユーザー領域のPython依存ライブラリを修復中...");
                await InstallBackendRequirementsAsync(userVenvPath, pythonBackendDir, cancellationToken);
                if (!await CheckCommandExistsAsync(userVenvPath,
                    GetPythonValidationArguments(), cancellationToken))
                    throw new InvalidOperationException("Python環境の検証に失敗しました。python_backend/requirements.txtと診断レポートを確認してください。");
                return;
            }

            if (File.Exists(packagedVenvPath))
            {
                bool ready = await CheckCommandExistsAsync(packagedVenvPath,
                    GetPythonValidationArguments(), cancellationToken);
                if (ready) return;
                operation?.Update(0.04f, "同梱Python環境が不完全です。ユーザー領域へ新しい環境を作成します...");
            }

            bool hasPython = await CheckCommandExistsAsync("python",
                "-c \"import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)\"", cancellationToken);
            string pythonCommand = "python";
            string pythonPrefix = string.Empty;
            if (!hasPython)
            {
                hasPython = await CheckCommandExistsAsync("py", "-3.12 --version", cancellationToken);
                pythonCommand = "py";
                pythonPrefix = "-3.12 ";
            }
            if (!hasPython)
                throw new InvalidOperationException("対応するPython 3.12が見つかりません。Python 3.12をユーザー権限で導入するか、オフラインwheelhouseを使ってpython_backend/Setup-Python.ps1を実行してください。");

            operation?.Update(0.05f, "ユーザー領域にPython仮想環境を作成中...");
            Directory.CreateDirectory(GetUserVenvDirectory());

            string venvArguments = pythonPrefix + "-m venv \"" + GetUserVenvDirectory() + "\"";
            bool venvSuccess = await RunCommandAsync(pythonCommand, venvArguments, pythonBackendDir, cancellationToken);
            if (!venvSuccess)
            {
                throw new Exception("ユーザー領域のPython仮想環境を作成できませんでした。空き容量とPython 3.12のvenv機能を確認してください。");
            }

            if (!File.Exists(userVenvPath))
            {
                throw new Exception("Python仮想環境の作成後にpython.exeが見つかりませんでした。");
            }

            operation?.Update(0.3f, "固定バージョンの依存ライブラリをユーザー領域へ導入中...");
            await InstallBackendRequirementsAsync(userVenvPath, pythonBackendDir, cancellationToken);
            if (!await CheckCommandExistsAsync(userVenvPath,
                GetPythonValidationArguments(), cancellationToken))
                throw new InvalidOperationException("インストール後のPython依存ライブラリ検証に失敗しました。");
            operation?.Update(0.99f, "ユーザー領域のPython環境を確認しました。");
        }

        private static async Task InstallBackendRequirementsAsync(string pythonPath, string backendDir,
            CancellationToken cancellationToken)
        {
            string requirementsPath = Path.Combine(backendDir, "requirements.txt");
            if (!File.Exists(requirementsPath))
                throw new FileNotFoundException("Python依存関係一覧がありません。", requirementsPath);
            string wheelhouse = Path.Combine(backendDir, "wheelhouse");
            string installArguments = Directory.Exists(wheelhouse)
                ? "-m pip install --no-index --find-links \"wheelhouse\" -r requirements.txt"
                : "-m pip install -r requirements.txt";
            await RunCommandAsync(pythonPath, installArguments, backendDir, cancellationToken);
        }

        private static string GetPythonValidationArguments()
        {
            return "-c \"import sys,importlib.metadata as m,numpy,scipy,open3d,fastapi,uvicorn,pydantic,matplotlib; " +
                "expected={'open3d':'0.20.0','numpy':'2.5.3','scipy':'1.18.1','fastapi':'0.143.0','uvicorn':'0.54.0','pydantic':'2.14.0','matplotlib':'3.11.2'}; " +
                "assert sys.version_info[:2]==(3,12) and all(m.version(k)==v for k,v in expected.items())\"";
        }

        private static async Task<bool> CheckCommandExistsAsync(string command, string arguments, CancellationToken cancellationToken)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    try
                    {
                        while (!p.HasExited)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await Task.Delay(100);
                        }
                        await Task.Run(() => p.WaitForExit());
                    }
                    catch (OperationCanceledException)
                    {
                        await TerminateAndWaitAsync(p);
                        throw;
                    }
                    return p.ExitCode == 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> RunCommandAsync(string command, string arguments, string workingDir, CancellationToken cancellationToken)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var stdout = new BoundedTextBuffer();
            var stderr = new BoundedTextBuffer();
            object logLock = new object();
            using (Process process = new Process())
            {
                process.StartInfo = psi;
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (logLock) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (logLock) stderr.AppendLine(e.Data); };
                if (!process.Start()) throw new InvalidOperationException($"プロセスを開始できません: {command}");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await TerminateAndWaitAsync(process);
                        throw new OperationCanceledException(cancellationToken);
                    }
                    await Task.Delay(100);
                }
                await Task.Run(() => process.WaitForExit());
                if (process.ExitCode != 0)
                {
                    string outText;
                    string errText;
                    lock (logLock) { outText = stdout.ToString(); errText = stderr.ToString(); }
                    throw new InvalidOperationException(
                        $"Process failed (ExitCode: {process.ExitCode}): {command} {arguments}\n[stderr]\n{errText}\n[stdout]\n{outText}");
                }
                return true;
            }
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
            PointData[] points,
            float coordinateScaleToMm,
            PointCloudOperation operation,
            CancellationToken cancellationToken = default)
        {
            if (float.IsNaN(coordinateScaleToMm) || float.IsInfinity(coordinateScaleToMm) || coordinateScaleToMm <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(coordinateScaleToMm), "座標からmmへの倍率は正の有限値である必要があります。");
            }
            await EnsureEnvironmentReadyAsync(cancellationToken, operation);

            string pythonPath = GetPythonPath();
            string scriptPath = GetScriptPath();

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Python繝弱う繧ｺ繝輔ぅ繝ｫ繧ｿ繧ｹ繧ｯ繝ｪ繝励ヨ縺瑚ｦ九▽縺九ｊ縺ｾ縺帙ｓ: {scriptPath}");
            }

            // 出力ディレクトリの作成
            Directory.CreateDirectory(outputDir);
            string operationId = Guid.NewGuid().ToString("N");

            // 削除済みフラグのマスクを書き出す
            string deletedMaskPath = Path.Combine(outputDir, "deleted_mask." + operationId + ".bin");
            string configJsonPath = Path.Combine(outputDir, "pipeline_config." + operationId + ".json");
            try
            {
            if (points != null && points.Length > 0)
            {
                byte[] deletedMask = new byte[points.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    deletedMask[i] = (points[i].label & 0x20000) != 0 ? (byte)1 : (byte)0;
                }
                // 非同期で書き込み
                using (var fs = new FileStream(deletedMaskPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                {
                    await fs.WriteAsync(deletedMask, 0, deletedMask.Length, cancellationToken);
                }
            }

            // 引数の構築
            string arguments = BuildArguments(scriptPath, inputPlyPath, outputDir, filterParams,
                deletedMaskPath, configJsonPath, coordinateScaleToMm, operationId);

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

                BoundedTextBuffer outputLog = new BoundedTextBuffer();
                BoundedTextBuffer errorLog = new BoundedTextBuffer();
                object logLock = new object();

                // 最後の活動時刻（処理時間はUTCの現在時刻のTick数）
                long lastActivityTicks = System.DateTime.UtcNow.Ticks;

                // 出力を非同期で読み出し、進捗ステータスを更新する
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        lock (logLock) outputLog.AppendLine(e.Data);
                        
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
                                    operation?.Update(mappedProgress, msgStr);
                                }
                            }
                        }
                        else if (e.Data.Contains("PLYファイルをロード中") || e.Data.Contains("PLY繝輔ぃ繧､繝ｫ繧偵Ο繝ｼ繝我ｸｭ"))
                        {
                            operation?.Update(0.1f, "点群データをPythonへロード中...");
                        }
                        else if (e.Data.Contains("処理を開始します") || e.Data.Contains("蜃ｦ逅ｒ髢句ｧ九＠縺ｾ縺"))
                        {
                            operation?.Update(0.2f, "ノイズ除去処理を開始します...");
                        }
                        else if (e.Data.Contains("結果出力ディレクトリ") || e.Data.Contains("邨先棡蜃ｺ蜉帙ョ繧｣繝ｬ繧ｯ繝医Μ"))
                        {
                            operation?.Update(0.8f, "結果バイナリデータを保存中...");
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        lock (logLock) errorLog.AppendLine(e.Data);
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
                        await TerminateAndWaitAsync(process);
                        throw new OperationCanceledException(cancellationToken);
                    }

                    // 辟｡騾壻ｿ｡繧ｿ繧､繝繧｢繧ｦ繝域､懃衍域怙蠕後繝ｭ繧ｰ縺九ｉ180遘堤ｵ碁℃
                    long lastTicks = System.Threading.Interlocked.Read(ref lastActivityTicks);
                    double idleSeconds = (System.DateTime.UtcNow.Ticks - lastTicks) / (double)System.TimeSpan.TicksPerSecond;

                    if (idleSeconds > timeoutSeconds)
                    {
                        await TerminateAndWaitAsync(process);
                        string outputText;
                        string errorText;
                        lock (logLock) { outputText = outputLog.ToString(); errorText = errorLog.ToString(); }
                        throw new TimeoutException($"Pythonノイズフィルタの処理が {timeoutSeconds}秒 間ログを出力せず応答しませんでした。\n[出力ログ]\n{outputText}\n[エラーログ]\n{errorText}");
                    }

                    // 100msウェイト
                    await Task.Delay(100);
                }

                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode != 0)
                {
                    string errText;
                    string outText;
                    lock (logLock) { errText = errorLog.ToString(); outText = outputLog.ToString(); }
                    throw new Exception($"Pythonノイズフィルタがエラーで終了しました (ExitCode: {process.ExitCode})\n[エラーログ]\n{errText}\n[出力ログ]\n{outText}");
                }
            }

            // 終了後にバイナリファイルを読み込み
            operation?.Update(0.9f, "バイナリ結果データをロード中...");
            string generationDirectory = OutputGenerationStore.ResolveGeneration(outputDir, operationId);
            string metadataPath = Path.Combine(generationDirectory, "metadata.json");
            string metadataJson = await Task.Run(() => File.ReadAllText(metadataPath), cancellationToken);
            NoiseFilterMetadata metadata = JsonUtility.FromJson<NoiseFilterMetadata>(metadataJson);
            if (metadata == null || metadata.point_count < 0 || metadata.operation_id != operationId)
                throw new InvalidDataException("ノイズ解析metadata.jsonの形式または点数が不正です。");
            return await Task.Run(() => LoadFilterResult(generationDirectory, metadata), cancellationToken);
            }
            finally
            {
                TryDeleteTemporaryFile(deletedMaskPath);
                TryDeleteTemporaryFile(configJsonPath);
            }
        }

        /// <summary>
        /// ダウンサンプリングスクリプトを非同期実行します。
        /// </summary>
        public static async Task<bool> RunDownsamplingAsync(
            string inputDir,
            string outputDir,
            float voxelSizeMm,
            float coordinateScaleToMm,
            string mergedOutputPath,
            PointCloudOperation operation,
            int mode = 1,
            CancellationToken cancellationToken = default)
        {
            if (float.IsNaN(coordinateScaleToMm) || float.IsInfinity(coordinateScaleToMm) || coordinateScaleToMm <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(coordinateScaleToMm), "座標からmmへの倍率は正の有限値である必要があります。");
            }
            string pythonPath = GetPythonPath();
            string scriptPath = GetDownsampleScriptPath();
            string projectRoot = GetProjectRootPath();
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"ダウンサンプリングスクリプトが見つかりません: {scriptPath}");
            }
            if (string.IsNullOrWhiteSpace(mergedOutputPath))
            {
                throw new ArgumentException("統合PLYの出力先を指定してください。", nameof(mergedOutputPath));
            }

            // 引数の構築
            string arguments = $"-u \"{scriptPath}\" --input \"{inputDir}\" --output \"{outputDir}\" --mode {mode} --voxel_size {voxelSizeMm.ToString(System.Globalization.CultureInfo.InvariantCulture)} --coordinate-scale-to-mm {coordinateScaleToMm.ToString(System.Globalization.CultureInfo.InvariantCulture)} --merged-output \"{mergedOutputPath}\"";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = arguments,
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            UnityEngine.Debug.Log($"[PythonBridge] ダウンサンプリング実行コマンド: {pythonPath} {psi.Arguments}");
            operation?.Update(0.05f, "ダウンサンプリング処理を開始中...");

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                BoundedTextBuffer outputLog = new BoundedTextBuffer();
                BoundedTextBuffer errorLog = new BoundedTextBuffer();
                object logLock = new object();

                long lastActivityTicks = System.DateTime.UtcNow.Ticks;
                const int timeoutSeconds = 300; // 5分

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        System.Threading.Interlocked.Exchange(ref lastActivityTicks, System.DateTime.UtcNow.Ticks);
                        lock (logLock) outputLog.AppendLine(e.Data);

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
                                    operation?.Update(mappedProgress, msgStr);
                                }
                            }
                            else
                            {
                                if (float.TryParse(partsStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float pct))
                                {
                                    float mappedProgress = pct / 100f;
                                    operation?.Update(mappedProgress, "実行中...");
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
                        lock (logLock) errorLog.AppendLine(e.Data);
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
                        await TerminateAndWaitAsync(process);
                        throw new OperationCanceledException(cancellationToken);
                    }

                    long lastTicks = System.Threading.Interlocked.Read(ref lastActivityTicks);
                    double idleSeconds = (System.DateTime.UtcNow.Ticks - lastTicks) / (double)System.TimeSpan.TicksPerSecond;
                    if (idleSeconds > timeoutSeconds)
                    {
                        await TerminateAndWaitAsync(process);
                        string outText;
                        string errText;
                        lock (logLock) { outText = outputLog.ToString(); errText = errorLog.ToString(); }
                        throw new TimeoutException($"ダウンサンプリング処理がタイムアウトしました。\n[stderr]\n{errText}\n[stdout]\n{outText}");
                    }

                    await Task.Delay(100);
                }

                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode != 0)
                {
                    string outText;
                    string errText;
                    lock (logLock) { outText = outputLog.ToString(); errText = errorLog.ToString(); }
                    throw new Exception($"ダウンサンプリングがエラーで終了しました (ExitCode: {process.ExitCode})\n[stderr]\n{errText}\n[stdout]\n{outText}");
                }
            }

            operation?.Update(1.0f, "ダウンサンプリングが完了しました。");
            return true;
        }

        /// <summary>
        /// NoiseFilterParams オブジェクトから Python スクリプト実行用のコマンドライン引数を構築します。
        /// 同時に、順序と個別パラメータを含んだ JSON 構成ファイルを保存し、引数で渡します。
        /// </summary>
        private static string BuildArguments(string scriptPath, string inputPlyPath, string outputDir, NoiseFilterParams p,
            string deletedMaskPath, string configJsonPath, float coordinateScaleToMm, string operationId)
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
            try
            {
                File.WriteAllText(configJsonPath, jsonBuilder.ToString());
            }
            catch (Exception ex)
            {
                throw new IOException($"パイプライン構成JSONを書き込めませんでした: {configJsonPath}", ex);
            }

            StringBuilder argsBuilder = new StringBuilder();
            argsBuilder.Append("-u "); // Pythonの出力をバッファリングせずリアルタイムに出力させる
            argsBuilder.Append($"\"{scriptPath}\"");
            argsBuilder.Append($" --input \"{inputPlyPath}\"");
            argsBuilder.Append($" --output_dir \"{outputDir}\"");
            argsBuilder.Append($" --config_json \"{configJsonPath}\"");
            argsBuilder.Append($" --coordinate-scale-to-mm {coordinateScaleToMm.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            argsBuilder.Append($" --operation_id {QuoteCommandLineArgument(operationId)}");

            if (!string.IsNullOrEmpty(deletedMaskPath) && File.Exists(deletedMaskPath))
            {
                argsBuilder.Append($" --deleted_mask \"{deletedMaskPath}\"");
            }

            return argsBuilder.ToString();
        }

        private static void TryDeleteTemporaryFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"[RecoverableOperationError] Python入力一時ファイルを削除できませんでした: {path}\n{exception.Message}");
            }
        }

        /// <summary>
        /// 謖�ｮ壹＆繧後◆蜃ｺ蜉帙ョ繧｣繝ｬ繧ｯ繝医Μ縺ｮ繝舌う繝翫Μ繝輔ぃ繧､繝ｫ縺翫ｈ縺ｳJSON繧帝ｫ倬溘Ο繝ｼ繝峨＠縺ｾ縺吶�
        /// </summary>
        private static NoiseFilterResult LoadFilterResult(string outputDir, NoiseFilterMetadata meta)
        {
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
