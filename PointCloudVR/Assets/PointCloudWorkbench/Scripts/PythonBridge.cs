using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PointCloudWorkbench
{
    [Serializable]
    public sealed class PythonEnvironmentPackageInfo
    {
        public string name;
        public string version;
        public bool import_ok;
    }

    public sealed class PythonEnvironmentInfo
    {
        public bool is_compatible;
        public string python_path;
        public string python_version;
        public string message;
        public PythonEnvironmentPackageInfo[] packages;
    }

    public sealed class PythonEnvironmentSetupRequiredException : Exception
    {
        public PythonEnvironmentInfo EnvironmentInfo { get; }
        public string SetupReason { get; }
        public string DiagnosticDetail { get; }

        public PythonEnvironmentSetupRequiredException(PythonEnvironmentInfo environmentInfo)
            : base(BuildSetupReason(environmentInfo))
        {
            EnvironmentInfo = environmentInfo;
            SetupReason = Message;
            DiagnosticDetail = BuildDiagnosticDetail(environmentInfo);
        }

        private static string BuildSetupReason(PythonEnvironmentInfo info)
        {
            var missingPackages = new List<string>();
            if (info != null && info.packages != null)
            {
                for (int i = 0; i < info.packages.Length; i++)
                {
                    PythonEnvironmentPackageInfo package = info.packages[i];
                    if (package != null && !package.import_ok && !string.IsNullOrWhiteSpace(package.name))
                        missingPackages.Add(package.name);
                }
            }

            string reason = missingPackages.Count > 0
                ? "不足または読み込めないライブラリ: " + string.Join("、", missingPackages.ToArray())
                : (info != null && !string.IsNullOrWhiteSpace(info.message)
                    ? info.message
                    : "Python 3.12または必要なライブラリを利用できません。");
            return "Python環境のセットアップが必要です。\n" + reason;
        }

        private static string BuildDiagnosticDetail(PythonEnvironmentInfo info)
        {
            if (info == null) return "Python environment validation returned no result.";
            var detail = new StringBuilder();
            detail.AppendLine("Python path: " + (info.python_path ?? "not found"));
            detail.AppendLine("Python version: " + (info.python_version ?? "unknown"));
            detail.AppendLine("Validation: " + (info.message ?? string.Empty));
            if (info.packages != null)
            {
                for (int i = 0; i < info.packages.Length; i++)
                {
                    PythonEnvironmentPackageInfo package = info.packages[i];
                    if (package == null) continue;
                    detail.AppendLine(package.name + " " + (package.version ?? "unknown") +
                        " import_ok=" + package.import_ok);
                }
            }
            return detail.ToString();
        }
    }

    public sealed class PythonEnvironmentSetupSnapshot
    {
        public bool is_running;
        public bool is_complete;
        public bool was_cancelled;
        public bool has_error;
        public int stage_index;
        public string stage;
        public string python_path;
        public string setup_directory;
        public string log_path;
        public string pip_log_path;
        public string error;
        public string recent_log;
        public long log_revision;
        public string pip_state;
        public string pip_progress_mode;
        public string pip_progress_note;
        public string pip_package;
        public int pip_process_id;
        public bool pip_process_alive;
        public bool pip_progress_available;
        public bool pip_cpu_available;
        public bool pip_io_available;
        public long pip_downloaded_bytes;
        public long pip_total_bytes;
        public long pip_read_bytes_delta;
        public long pip_write_bytes_delta;
        public double pip_cpu_seconds_delta;
        public double elapsed_seconds;
        public double seconds_since_output;
        public double seconds_since_stdout;
        public double seconds_since_stderr;
        public double seconds_since_progress;
    }

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
        private static volatile string selectedPythonPath;
        [Serializable]
        private sealed class PythonEnvironmentManifest
        {
            public string python_executable;
            public string requirements_sha256;
            public string validated_utc;
        }

        private static readonly object environmentSetupLock = new object();
        private static readonly BoundedTextBuffer environmentSetupTail = new BoundedTextBuffer(96 * 1024, 4 * 1024);
        private static readonly string[] environmentSetupStages =
        {
            "Python 3.12の確認", "仮想環境の作成", "依存ライブラリの導入", "動作検証"
        };
        private static Process environmentSetupProcess;
        private static CancellationTokenSource environmentSetupCancellation;
        private static Task environmentSetupTask;
        private static StreamWriter environmentSetupLogWriter;
        private static DateTime environmentSetupStartedUtc;
        private static DateTime environmentSetupLastOutputUtc;
        private static DateTime environmentSetupLastStdoutUtc;
        private static DateTime environmentSetupLastStderrUtc;
        private static DateTime environmentSetupLastProgressUtc;
        private static DateTime environmentSetupLastProgressLogUtc;
        private static DateTime environmentSetupLastPipSampleUtc;
        private static int environmentSetupPipProcessId;
        private static bool environmentSetupPipProcessAlive;
        private static bool environmentSetupPipProgressAvailable;
        private static bool environmentSetupPipCpuAvailable;
        private static bool environmentSetupPipIoAvailable;
        private static long environmentSetupPipDownloadedBytes;
        private static long environmentSetupPipTotalBytes;
        private static long environmentSetupPipPreviousReadBytes;
        private static long environmentSetupPipPreviousWriteBytes;
        private static double environmentSetupPipCpuSeconds;
        private static double environmentSetupPipCpuSecondsDelta;
        private static long environmentSetupPipReadBytesDelta;
        private static long environmentSetupPipWriteBytesDelta;
        private static string environmentSetupPipState = "未起動";
        private static string environmentSetupPipProgressMode = string.Empty;
        private static string environmentSetupPipProgressNote = string.Empty;
        private static string environmentSetupPipPackage = string.Empty;
        private static string environmentSetupPipLogPath = string.Empty;
        private static bool environmentSetupLogLimitNotified;
        private static bool environmentSetupPreparedOnly;
        private static bool environmentSetupRunning;
        private static bool environmentSetupComplete;
        private static bool environmentSetupCancelled;
        private static int environmentSetupStageIndex;
        private static string environmentSetupStage;
        private static string environmentSetupPythonPath;
        private static string environmentSetupDirectory;
        private static string environmentSetupLogPath;
        private static string environmentSetupError;
        private static long environmentSetupRevision;
        private static long environmentSetupCachedRevision = -1;
        private static string environmentSetupCachedTail = string.Empty;
        private static PythonEnvironmentInfo lastEnvironmentInfo;

        private sealed class SetupProcessJob : IDisposable
        {
            private const uint KillOnJobClose = 0x00002000;
            private const uint ProcessQueryInformation = 0x0400;
            private const int JobObjectExtendedLimitInformationClass = 9;
            private IntPtr handle;

            [StructLayout(LayoutKind.Sequential)]
            private struct BasicLimitInformation
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ExtendedLimitInformation
            {
                public BasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
                ref ExtendedLimitInformation information, uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

            private SetupProcessJob(IntPtr jobHandle)
            {
                handle = jobHandle;
            }

            public static SetupProcessJob Create()
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                        "Pythonセットアップ用のプロセス管理を作成できませんでした。");

                ExtendedLimitInformation information = new ExtendedLimitInformation();
                information.BasicLimitInformation.LimitFlags = KillOnJobClose;
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass,
                    ref information, (uint)Marshal.SizeOf(typeof(ExtendedLimitInformation))))
                {
                    int error = Marshal.GetLastWin32Error();
                    CloseHandle(job);
                    throw new System.ComponentModel.Win32Exception(error,
                        "Pythonセットアップの終了時クリーンアップを設定できませんでした。");
                }

                return new SetupProcessJob(job);
            }

            public void Assign(Process process)
            {
                if (handle == IntPtr.Zero || process == null || !AssignProcessToJobObject(handle, process.Handle))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                        "PowerShellをPlayer所有のプロセス管理へ登録できませんでした。");
            }

            public bool Terminate(uint exitCode)
            {
                return handle != IntPtr.Zero && TerminateJobObject(handle, exitCode);
            }

            public static bool TryGetProcessIoCounters(int processId, out long readBytes, out long writeBytes)
            {
                readBytes = 0;
                writeBytes = 0;
                IntPtr process = OpenProcess(ProcessQueryInformation, false, (uint)processId);
                if (process == IntPtr.Zero) return false;
                try
                {
                    IoCounters counters;
                    if (!GetProcessIoCounters(process, out counters)) return false;
                    readBytes = unchecked((long)counters.ReadTransferCount);
                    writeBytes = unchecked((long)counters.WriteTransferCount);
                    return true;
                }
                finally
                {
                    CloseHandle(process);
                }
            }

            public void Dispose()
            {
                IntPtr closing = handle;
                handle = IntPtr.Zero;
                if (closing != IntPtr.Zero) CloseHandle(closing);
            }
        }

        /// <summary>
        /// 螳溯｡檎腸蠅�↓縺翫￠繧倶ｻｮ諠ｳ迺ｰ蠅�� python.exe 縺ｮ繝代せ繧貞叙蠕励＠縺ｾ縺吶�
        /// 蟄伜惠縺励↑縺��ｴ蜷医�繧ｷ繧ｹ繝�Β迺ｰ蠅�ヱ繧ｹ縺ｮ python.exe 繧呈爾縺励∪縺吶�
        /// </summary>
        public static string GetPythonPath()
        {
            if (!string.IsNullOrWhiteSpace(selectedPythonPath) && File.Exists(selectedPythonPath))
                return selectedPythonPath;

            string activatedPython = ReadActivatedPythonPath();
            if (!string.IsNullOrWhiteSpace(activatedPython) && File.Exists(activatedPython)) return activatedPython;
            string packagedVenv = GetPackagedVenvPythonPath();
            if (File.Exists(packagedVenv)) return packagedVenv;
            string userVenv = GetUserVenvPythonPath();
            if (File.Exists(userVenv)) return userVenv;
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

        private static string GetPythonEnvironmentRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.persistentDataPath, "PythonEnvironment"));
        }

        private static string GetManagedEnvironmentRoot()
        {
            return Path.Combine(GetPythonEnvironmentRoot(), "managed");
        }

        private static string GetEnvironmentManifestPath()
        {
            return Path.Combine(GetPythonEnvironmentRoot(), "active.json");
        }

        private static string ReadActivatedPythonPath()
        {
            try
            {
                string manifestPath = GetEnvironmentManifestPath();
                if (!File.Exists(manifestPath)) return null;
                PythonEnvironmentManifest manifest = JsonUtility.FromJson<PythonEnvironmentManifest>(
                    File.ReadAllText(manifestPath, Encoding.UTF8));
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.python_executable)) return null;
                string fullPath = Path.GetFullPath(manifest.python_executable);
                string managedRoot = Path.GetFullPath(GetManagedEnvironmentRoot()) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase)) return null;
                return fullPath;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[PythonBridge] Python環境の登録情報を読み込めません: " + ex.Message);
                return null;
            }
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

            bool exited = await Task.Run(() => process.WaitForExit(10000));
            if (!exited) throw new TimeoutException("Python子プロセスがキャンセル後10秒以内に終了しませんでした。");
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

        [Serializable]
        private sealed class PythonEnvironmentProbeJson
        {
            public bool is_compatible;
            public string python_executable;
            public string python_version;
            public string message;
            public PythonEnvironmentPackageInfo[] packages;
        }

        private sealed class PythonCommandCandidate
        {
            public readonly string FileName;
            public readonly string PrefixArguments;

            public PythonCommandCandidate(string fileName, string prefixArguments = "")
            {
                FileName = fileName;
                PrefixArguments = prefixArguments;
            }
        }

        /// <summary>現在利用できる環境を一度だけ検証し、診断と解析で同じ実行ファイルを共有します。</summary>
        public static async Task<PythonEnvironmentInfo> ValidateAvailableEnvironmentAsync(CancellationToken cancellationToken)
        {
            string backendDirectory = GetPythonBackendDirectory();
            List<PythonCommandCandidate> candidates = BuildPythonCandidates(
                selectedPythonPath, ReadActivatedPythonPath(), GetPackagedVenvPythonPath(), GetUserVenvPythonPath());
            PythonEnvironmentInfo lastFailure = null;
            string requirementsPath = Path.Combine(backendDirectory, "requirements.txt");
            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PythonEnvironmentInfo info = await ProbePythonCandidateAsync(
                    candidates[i], requirementsPath, backendDirectory, cancellationToken, 30000);
                if (info == null) continue;
                if (!info.is_compatible)
                {
                    if (!string.IsNullOrWhiteSpace(info.python_path) || lastFailure == null) lastFailure = info;
                    continue;
                }

                selectedPythonPath = info.python_path;
                lastEnvironmentInfo = info;
                return info;
            }

            if (lastFailure == null ||
                (string.IsNullOrWhiteSpace(lastFailure.python_path) && string.IsNullOrWhiteSpace(lastFailure.python_version)))
            {
                lastEnvironmentInfo = new PythonEnvironmentInfo
                {
                    is_compatible = false,
                    message = "Python 3.12の実行環境が見つかりません。Python本体を導入してから再確認してください。",
                    packages = new PythonEnvironmentPackageInfo[0]
                };
            }
            else lastEnvironmentInfo = lastFailure;
            return lastEnvironmentInfo;
        }

        private static List<PythonCommandCandidate> BuildPythonCandidates(
            string selectedPython, string activatedPython, string packagedPython, string userPython)
        {
            List<PythonCommandCandidate> candidates = new List<PythonCommandCandidate>();
            AddPythonPathCandidate(candidates, selectedPython);
            AddPythonPathCandidate(candidates, activatedPython);
            AddPythonPathCandidate(candidates, packagedPython);
            AddPythonPathCandidate(candidates, userPython);
            candidates.Add(new PythonCommandCandidate("python"));
            candidates.Add(new PythonCommandCandidate("py", "-3.12 "));
            return candidates;
        }

        public static PythonEnvironmentInfo GetLastEnvironmentInfo()
        {
            return lastEnvironmentInfo;
        }

        public static async Task EnsureEnvironmentReadyAsync(CancellationToken cancellationToken, PointCloudOperation operation = null)
        {
            operation?.Update(0.01f, "Python環境を確認中...");
            PythonEnvironmentInfo info = await ValidateAvailableEnvironmentAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (info == null || !info.is_compatible)
                throw new PythonEnvironmentSetupRequiredException(info);
            operation?.Update(0.99f, "Python環境を確認しました。");
        }

        private static string GetPythonBackendDirectory()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend"));
        }

        private static void AddPythonPathCandidate(List<PythonCommandCandidate> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            for (int i = 0; i < candidates.Count; i++)
                if (string.Equals(candidates[i].FileName, path, StringComparison.OrdinalIgnoreCase)) return;
            candidates.Add(new PythonCommandCandidate(path));
        }

        private static async Task<PythonEnvironmentInfo> ProbePythonCandidateAsync(
            PythonCommandCandidate candidate, string requirementsPath, string workingDirectory, CancellationToken cancellationToken,
            int timeoutMilliseconds)
        {
            string mplConfigPath = Path.Combine(Path.GetTempPath(), "pcwb-python-probe-" + Guid.NewGuid().ToString("N"));
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = candidate.FileName,
                Arguments = candidate.PrefixArguments + "-B -c " + QuoteCommandLineArgument(GetPythonValidationScript()) +
                    " " + QuoteCommandLineArgument(requirementsPath),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            startInfo.EnvironmentVariables["MPLBACKEND"] = "Agg";
            startInfo.EnvironmentVariables["MPLCONFIGDIR"] = mplConfigPath;

            try
            {
                using (Process process = new Process { StartInfo = startInfo })
                {
                    if (!process.Start()) return null;
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
                    while (!process.HasExited)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            await TerminateAndWaitAsync(process);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            await TerminateAndWaitAsync(process);
                            return new PythonEnvironmentInfo
                            {
                                is_compatible = false,
                                message = $"Python環境の確認が{timeoutMilliseconds / 1000}秒でタイムアウトしました: {candidate.FileName}"
                            };
                        }
                        await Task.Delay(100);
                    }
                    await Task.Run(() => process.WaitForExit());
                    await Task.WhenAll(stdoutTask, stderrTask);
                    return ParsePythonEnvironmentProbe(stdoutTask.Result, stderrTask.Result, process.ExitCode);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new PythonEnvironmentInfo { is_compatible = false, message = ex.Message };
            }
            finally
            {
                try
                {
                    string tempPath = Path.GetFullPath(mplConfigPath);
                    string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (tempPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(tempPath).StartsWith("pcwb-python-probe-", StringComparison.Ordinal) &&
                        Directory.Exists(tempPath))
                        Directory.Delete(tempPath, true);
                }
                catch { }
            }
        }

        private static string GetPythonValidationScript()
        {
            StringBuilder script = new StringBuilder(4096);
            script.AppendLine("import importlib, importlib.metadata as md, json, sys");
            script.AppendLine("modules = {'numpy':'numpy','scipy':'scipy','open3d':'open3d','fastapi':'fastapi','uvicorn':'uvicorn','pydantic':'pydantic','matplotlib':'matplotlib'}");
            script.AppendLine("legacy = {'numpy':{'2.4.6'},'scipy':{'1.17.1'},'open3d':{'0.19.0'},'fastapi':{'0.136.3'},'uvicorn':{'0.48.0'},'pydantic':{'2.13.4'},'matplotlib':{'3.11.2'}}");
            script.AppendLine("pins = {}");
            script.AppendLine("try:");
            script.AppendLine("    for raw in open(sys.argv[1], encoding='utf-8'):");
            script.AppendLine("        line = raw.split('#', 1)[0].strip()");
            script.AppendLine("        if '==' in line:");
            script.AppendLine("            name, version = line.split('==', 1)");
            script.AppendLine("            pins[name.strip().lower().replace('_', '-')] = version.strip()");
            script.AppendLine("except Exception as e:");
            script.AppendLine("    pins = {}");
            script.AppendLine("    requirement_error = str(e)");
            script.AppendLine("packages = []");
            script.AppendLine("accepted = True");
            script.AppendLine("errors = []");
            script.AppendLine("for name, module in modules.items():");
            script.AppendLine("    try:");
            script.AppendLine("        importlib.import_module(module)");
            script.AppendLine("        version = md.version(name)");
            script.AppendLine("        allowed = {pins.get(name)} | legacy.get(name, set())");
            script.AppendLine("        ok = bool(pins.get(name)) and version in allowed");
            script.AppendLine("        packages.append({'name':name,'version':version,'import_ok':True})");
            script.AppendLine("        accepted = accepted and ok");
            script.AppendLine("        if not ok: errors.append(name + ' version ' + version + ' is not in the verified set')");
            script.AppendLine("    except Exception as e:");
            script.AppendLine("        packages.append({'name':name,'version':'unavailable','import_ok':False})");
            script.AppendLine("        accepted = False");
            script.AppendLine("        errors.append(name + ': ' + str(e))");
            script.AppendLine("if sys.version_info[:2] != (3, 12):");
            script.AppendLine("    accepted = False");
            script.AppendLine("    errors.append('Python 3.12 is required')");
            script.AppendLine("try:");
            script.AppendLine("    import numpy as np, open3d as o3d");
            script.AppendLine("    from scipy.spatial import cKDTree");
            script.AppendLine("    from scipy.sparse import csr_matrix");
            script.AppendLine("    from scipy.sparse.csgraph import connected_components");
            script.AppendLine("    from matplotlib.figure import Figure");
            script.AppendLine("    from matplotlib.backends.backend_agg import FigureCanvasAgg");
            script.AppendLine("    pts = np.array([[0.,0.,0.],[1.,0.,0.]])");
            script.AppendLine("    assert cKDTree(pts).query(pts)[0].max() == 0.");
            script.AppendLine("    assert connected_components(csr_matrix([[0,1],[1,0]]))[0] == 1");
            script.AppendLine("    cloud = o3d.geometry.PointCloud()");
            script.AppendLine("    cloud.points = o3d.utility.Vector3dVector(pts)");
            script.AppendLine("    assert len(cloud.voxel_down_sample(0.5).points) == 2");
            script.AppendLine("    fig = Figure()");
            script.AppendLine("    FigureCanvasAgg(fig)");
            script.AppendLine("    fig.subplots().plot([0,1],[0,1])");
            script.AppendLine("except Exception as e:");
            script.AppendLine("    accepted = False");
            script.AppendLine("    errors.append('API smoke test: ' + str(e))");
            script.AppendLine("result = {'is_compatible':accepted,'python_executable':sys.executable,'python_version':sys.version.split()[0],'message':'; '.join(errors) if errors else '環境と主要APIを確認しました。','packages':packages}");
            script.AppendLine("print('PCWB_ENVIRONMENT_JSON:' + json.dumps(result, ensure_ascii=False))");
            script.AppendLine("raise SystemExit(0 if accepted else 2)");
            return script.ToString();
        }

        private static PythonEnvironmentInfo ParsePythonEnvironmentProbe(string stdout, string stderr, int exitCode)
        {
            const string marker = "PCWB_ENVIRONMENT_JSON:";
            int offset = string.IsNullOrEmpty(stdout) ? -1 : stdout.LastIndexOf(marker, StringComparison.Ordinal);
            if (offset < 0)
                return new PythonEnvironmentInfo
                {
                    is_compatible = false,
                    message = $"Python環境検証が終了コード {exitCode} で失敗しました。{Environment.NewLine}{stderr}"
                };

            try
            {
                string json = stdout.Substring(offset + marker.Length).Trim();
                PythonEnvironmentProbeJson parsed = JsonUtility.FromJson<PythonEnvironmentProbeJson>(json);
                if (parsed == null) throw new InvalidDataException("検証JSONが空です。");
                string pythonPath = string.IsNullOrWhiteSpace(parsed.python_executable)
                    ? string.Empty
                    : Path.GetFullPath(parsed.python_executable);
                return new PythonEnvironmentInfo
                {
                    is_compatible = parsed.is_compatible && exitCode == 0 && File.Exists(pythonPath),
                    python_path = pythonPath,
                    python_version = parsed.python_version,
                    message = string.IsNullOrWhiteSpace(parsed.message) ? stderr : parsed.message,
                    packages = parsed.packages ?? new PythonEnvironmentPackageInfo[0]
                };
            }
            catch (Exception ex)
            {
                return new PythonEnvironmentInfo
                {
                    is_compatible = false,
                    message = "Python環境検証の応答を読み取れません: " + ex.Message + Environment.NewLine + stderr
                };
            }
        }

        public static PythonEnvironmentSetupSnapshot GetEnvironmentSetupSnapshot()
        {
            lock (environmentSetupLock)
            {
                return new PythonEnvironmentSetupSnapshot
                {
                    is_running = environmentSetupRunning,
                    is_complete = environmentSetupComplete,
                    was_cancelled = environmentSetupCancelled,
                    has_error = !string.IsNullOrEmpty(environmentSetupError),
                    stage_index = environmentSetupStageIndex,
                    stage = environmentSetupStage ?? string.Empty,
                    python_path = environmentSetupPythonPath ?? string.Empty,
                    setup_directory = environmentSetupDirectory ?? string.Empty,
                    log_path = environmentSetupLogPath ?? string.Empty,
                    pip_log_path = environmentSetupPipLogPath ?? string.Empty,
                    error = environmentSetupError ?? string.Empty,
                    recent_log = GetCachedEnvironmentSetupTail(),
                    log_revision = environmentSetupRevision,
                    pip_state = environmentSetupPipState ?? "不明",
                    pip_progress_mode = environmentSetupPipProgressMode ?? string.Empty,
                    pip_progress_note = environmentSetupPipProgressNote ?? string.Empty,
                    pip_package = environmentSetupPipPackage ?? string.Empty,
                    pip_process_id = environmentSetupPipProcessId,
                    pip_process_alive = environmentSetupPipProcessAlive,
                    pip_progress_available = environmentSetupPipProgressAvailable,
                    pip_cpu_available = environmentSetupPipCpuAvailable,
                    pip_io_available = environmentSetupPipIoAvailable,
                    pip_downloaded_bytes = environmentSetupPipDownloadedBytes,
                    pip_total_bytes = environmentSetupPipTotalBytes,
                    pip_read_bytes_delta = environmentSetupPipReadBytesDelta,
                    pip_write_bytes_delta = environmentSetupPipWriteBytesDelta,
                    pip_cpu_seconds_delta = environmentSetupPipCpuSecondsDelta,
                    elapsed_seconds = environmentSetupStartedUtc == default(DateTime)
                        ? 0d : (DateTime.UtcNow - environmentSetupStartedUtc).TotalSeconds,
                    seconds_since_output = environmentSetupLastOutputUtc == default(DateTime)
                        ? 0d : (DateTime.UtcNow - environmentSetupLastOutputUtc).TotalSeconds,
                    seconds_since_stdout = SecondsSince(environmentSetupLastStdoutUtc),
                    seconds_since_stderr = SecondsSince(environmentSetupLastStderrUtc),
                    seconds_since_progress = SecondsSince(environmentSetupLastProgressUtc)
                };
            }
        }

        private static double SecondsSince(DateTime timestampUtc)
        {
            return timestampUtc == default(DateTime)
                ? -1d
                : Math.Max(0d, (DateTime.UtcNow - timestampUtc).TotalSeconds);
        }

        public static void StartEnvironmentSetup()
        {
            string backendDirectory = GetPythonBackendDirectory();
            string environmentRoot = GetPythonEnvironmentRoot();
            List<PythonCommandCandidate> baseCandidates = BuildPythonCandidates(
                selectedPythonPath, ReadActivatedPythonPath(), GetPackagedVenvPythonPath(), GetUserVenvPythonPath());
            lock (environmentSetupLock)
            {
                if (environmentSetupRunning) return;
                environmentSetupCancellation?.Dispose();
                environmentSetupCancellation = new CancellationTokenSource();
                environmentSetupTail.Clear();
                environmentSetupRevision++;
                environmentSetupCachedRevision = -1;
                environmentSetupStartedUtc = DateTime.UtcNow;
                environmentSetupLastOutputUtc = environmentSetupStartedUtc;
                environmentSetupLastStdoutUtc = default(DateTime);
                environmentSetupLastStderrUtc = default(DateTime);
                environmentSetupLastProgressUtc = default(DateTime);
                environmentSetupLastProgressLogUtc = default(DateTime);
                environmentSetupLastPipSampleUtc = default(DateTime);
                environmentSetupPipProcessId = 0;
                environmentSetupPipProcessAlive = false;
                environmentSetupPipProgressAvailable = false;
                environmentSetupPipCpuAvailable = false;
                environmentSetupPipIoAvailable = false;
                environmentSetupPipDownloadedBytes = 0;
                environmentSetupPipTotalBytes = 0;
                environmentSetupPipPreviousReadBytes = 0;
                environmentSetupPipPreviousWriteBytes = 0;
                environmentSetupPipCpuSeconds = 0d;
                environmentSetupPipCpuSecondsDelta = 0d;
                environmentSetupPipReadBytesDelta = 0;
                environmentSetupPipWriteBytesDelta = 0;
                environmentSetupPipState = "未起動";
                environmentSetupPipProgressMode = string.Empty;
                environmentSetupPipProgressNote = string.Empty;
                environmentSetupPipPackage = string.Empty;
                environmentSetupPipLogPath = string.Empty;
                environmentSetupLogLimitNotified = false;
                environmentSetupPreparedOnly = false;
                environmentSetupRunning = true;
                environmentSetupComplete = false;
                environmentSetupCancelled = false;
                environmentSetupStageIndex = 0;
                environmentSetupStage = environmentSetupStages[0];
                environmentSetupPythonPath = string.Empty;
                environmentSetupDirectory = string.Empty;
                environmentSetupLogPath = string.Empty;
                environmentSetupError = string.Empty;
                CancellationToken token = environmentSetupCancellation.Token;
                environmentSetupTask = Task.Run(() => RunEnvironmentSetupAsync(
                    token, backendDirectory, environmentRoot, baseCandidates));
            }
        }

        public static void CancelEnvironmentSetup()
        {
            lock (environmentSetupLock)
            {
                if (!environmentSetupRunning) return;
                environmentSetupCancellation?.Cancel();
            }
        }

        private static async Task RunEnvironmentSetupAsync(CancellationToken cancellationToken, string backendDirectory,
            string environmentRoot, List<PythonCommandCandidate> baseCandidates)
        {
            try
            {
                string attemptId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                string setupRoot = Path.Combine(environmentRoot, "managed", "setup-" + attemptId);
                string logDirectory = Path.Combine(environmentRoot, "logs");
                Directory.CreateDirectory(logDirectory);
                string pipLogPath = Path.Combine(logDirectory, "pip-setup-" + attemptId + ".log");
                string retentionSummary = PruneEnvironmentSetupLogs(logDirectory);
                lock (environmentSetupLock)
                {
                    environmentSetupDirectory = setupRoot;
                    environmentSetupLogPath = Path.Combine(logDirectory, "python-setup-" + attemptId + ".log");
                    environmentSetupPipLogPath = pipLogPath;
                    environmentSetupLogWriter = new StreamWriter(environmentSetupLogPath, false, new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                }
                AppendEnvironmentSetupLog("INFO", "Python環境のセットアップを開始しました。既存環境は変更しません。");
                SetEnvironmentSetupStage(0, "Python 3.12の確認");
                AppendEnvironmentSetupLog("INFO", "セットアップ先: " + setupRoot);
                AppendEnvironmentSetupLog("INFO", "ログファイル: " + environmentSetupLogPath);
                if (!string.IsNullOrEmpty(retentionSummary))
                    AppendEnvironmentSetupLog("INFO", retentionSummary);
                string basePython = await FindPython312Async(baseCandidates, backendDirectory, cancellationToken);
                if (string.IsNullOrWhiteSpace(basePython))
                    throw new FileNotFoundException("Python 3.12が見つかりません。Python本体を導入してから再試行してください。公式案内: https://www.python.org/downloads/windows/");
                lock (environmentSetupLock) environmentSetupPythonPath = basePython;
                AppendEnvironmentSetupLog("INFO", "基礎Python: " + basePython);

                string setupScript = Path.Combine(backendDirectory, "Setup-Python.ps1");
                if (!File.Exists(setupScript)) throw new FileNotFoundException("Pythonセットアップスクリプトが見つかりません。", setupScript);
                string arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                    QuoteCommandLineArgument(setupScript) +
                    " -PythonExe " + QuoteCommandLineArgument(basePython) +
                    " -EnvironmentRoot " + QuoteCommandLineArgument(setupRoot) +
                    " -PipLogPath " + QuoteCommandLineArgument(pipLogPath) + " -PrepareOnly";
                string createdPython = Path.Combine(setupRoot, ".venv", "Scripts", "python.exe");
                using (SetupProcessJob job = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? SetupProcessJob.Create()
                    : null)
                {
                    int prepareExitCode = await RunEnvironmentSetupProcessAsync(
                        arguments, setupScript, setupRoot, job, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (prepareExitCode != 0)
                        throw new InvalidOperationException("Python環境の準備に失敗しました (ExitCode: " + prepareExitCode + ")。ログを確認してください。");

                    bool runPip;
                    string progressMode;
                    lock (environmentSetupLock)
                    {
                        runPip = environmentSetupPreparedOnly;
                        progressMode = environmentSetupPipProgressMode;
                    }
                    if (runPip)
                    {
                        SetEnvironmentSetupStage(2, environmentSetupStages[2]);
                        int pipExitCode = await RunPipInstallProcessAsync(
                            createdPython, Path.Combine(backendDirectory, "requirements.txt"), backendDirectory,
                            setupRoot, pipLogPath, progressMode, job, cancellationToken);
                        if (pipExitCode != 0)
                            throw new InvalidOperationException("pipの依存ライブラリ導入に失敗しました (ExitCode: " +
                                pipExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                ")。pip詳細ログを確認してください。");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                lock (environmentSetupLock)
                    if (!string.IsNullOrEmpty(environmentSetupError))
                        throw new IOException(environmentSetupError);

                SetEnvironmentSetupStage(3, "インストール後の動作検証");
                PythonEnvironmentInfo verified = await ProbePythonCandidateAsync(
                    new PythonCommandCandidate(createdPython), Path.Combine(backendDirectory, "requirements.txt"),
                    backendDirectory, cancellationToken, 60000);
                if (verified == null || !verified.is_compatible)
                    throw new InvalidOperationException("新しいPython環境の最終検証に失敗しました。" +
                        (verified == null ? string.Empty : Environment.NewLine + verified.message));

                WriteActivatedEnvironmentManifest(verified.python_path, environmentRoot,
                    Path.Combine(backendDirectory, "requirements.txt"));
                selectedPythonPath = verified.python_path;
                lastEnvironmentInfo = verified;
                lock (environmentSetupLock)
                {
                    environmentSetupPythonPath = verified.python_path;
                    environmentSetupStageIndex = environmentSetupStages.Length;
                    environmentSetupStage = "セットアップと検証が完了しました";
                    environmentSetupComplete = true;
                }
                AppendEnvironmentSetupLog("INFO", "検証済み環境を有効化しました: " + verified.python_path);
            }
            catch (OperationCanceledException)
            {
                lock (environmentSetupLock)
                {
                    environmentSetupCancelled = true;
                    environmentSetupStage = "キャンセルしました。未完成の環境は使用対象にしていません";
                    if (environmentSetupPipProcessAlive || environmentSetupPipProcessId > 0 &&
                        environmentSetupPipState == "終了コード未取得")
                    {
                        environmentSetupPipProcessAlive = false;
                        environmentSetupPipState = "キャンセル";
                    }
                }
                AppendEnvironmentSetupLog("CANCEL", "セットアップをキャンセルしました。既存環境と登録情報は保持されています。");
            }
            catch (Exception ex)
            {
                lock (environmentSetupLock)
                {
                    environmentSetupError = ex.ToString();
                    environmentSetupStage = "セットアップに失敗しました";
                    if (environmentSetupPipProcessAlive || environmentSetupPipProcessId > 0 &&
                        (environmentSetupPipState == "実行中" || environmentSetupPipState == "終了コード未取得"))
                    {
                        environmentSetupPipProcessAlive = false;
                        environmentSetupPipState = ex is TimeoutException ? "タイムアウト" : "失敗";
                    }
                }
                AppendEnvironmentSetupLog("ERROR", ex.ToString());
            }
            finally
            {
                Process process;
                string pipLogPath;
                lock (environmentSetupLock) pipLogPath = environmentSetupPipLogPath;
                SanitizePipLogFile(pipLogPath);
                lock (environmentSetupLock)
                {
                    process = environmentSetupProcess;
                    environmentSetupProcess = null;
                    environmentSetupRunning = false;
                    if (environmentSetupLogWriter != null)
                    {
                        environmentSetupLogWriter.Dispose();
                        environmentSetupLogWriter = null;
                    }
                    environmentSetupCancellation?.Dispose();
                    environmentSetupCancellation = null;
                }
                if (process != null) process.Dispose();
            }
        }

        private static async Task<string> FindPython312Async(List<PythonCommandCandidate> candidates,
            string backendDirectory, CancellationToken cancellationToken)
        {
            const string probe = "import os,sys; base=getattr(sys,'_base_executable',None) or sys.executable; print('PCWB_BASE:'+os.path.realpath(base)); raise SystemExit(0 if sys.version_info[:2]==(3,12) and os.path.isfile(base) else 1)";
            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PythonCommandCandidate candidate = candidates[i];
                AppendEnvironmentSetupLog("INFO", "Python 3.12候補を確認: " + candidate.FileName);
                try
                {
                    ProcessStartInfo info = new ProcessStartInfo
                    {
                        FileName = candidate.FileName,
                        Arguments = candidate.PrefixArguments + "-B -c " + QuoteCommandLineArgument(probe),
                        WorkingDirectory = backendDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    info.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
                    using (Process process = new Process { StartInfo = info })
                    {
                        if (!process.Start()) continue;
                        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                        while (!process.HasExited && DateTime.UtcNow < deadline)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                await TerminateAndWaitAsync(process);
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            await Task.Delay(100);
                        }
                        if (!process.HasExited)
                        {
                            await TerminateAndWaitAsync(process);
                            continue;
                        }
                        await Task.Run(() => process.WaitForExit());
                        await Task.WhenAll(stdoutTask, stderrTask);
                        if (process.ExitCode != 0)
                        {
                            string error = stderrTask.Result.Trim();
                            if (!string.IsNullOrEmpty(error)) AppendEnvironmentSetupLog("WARN", error);
                            continue;
                        }
                        string output = stdoutTask.Result;
                        int marker = output.LastIndexOf("PCWB_BASE:", StringComparison.Ordinal);
                        if (marker < 0) continue;
                        string path = output.Substring(marker + "PCWB_BASE:".Length).Trim();
                        if (File.Exists(path)) return Path.GetFullPath(path);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppendEnvironmentSetupLog("WARN", ex.Message); }
            }
            return null;
        }

        private static async Task<int> RunEnvironmentSetupProcessAsync(
            string arguments, string setupScript, string setupRoot, SetupProcessJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string setupTempDirectory = Path.Combine(setupRoot, ".tmp");
            Directory.CreateDirectory(setupTempDirectory);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(setupScript),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            startInfo.EnvironmentVariables["PIP_NO_INPUT"] = "1";
            startInfo.EnvironmentVariables["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            startInfo.EnvironmentVariables["TEMP"] = setupTempDirectory;
            startInfo.EnvironmentVariables["TMP"] = setupTempDirectory;
            startInfo.EnvironmentVariables["TMPDIR"] = setupTempDirectory;

            Process process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) AppendEnvironmentSetupOutput("OUT", e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendEnvironmentSetupOutput("ERR", e.Data); };
            lock (environmentSetupLock) environmentSetupProcess = process;
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Pythonセットアップ用PowerShellを起動できませんでした。");
                if (job != null)
                {
                    try
                    {
                        job.Assign(process);
                    }
                    catch
                    {
                        await TerminateSetupProcessTreeAsync(process, null);
                        throw;
                    }
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                DateTime deadline = DateTime.UtcNow.AddHours(1);
                DateTime nextPipSampleUtc = DateTime.UtcNow;
                while (!process.HasExited)
                {
                    if (DateTime.UtcNow >= nextPipSampleUtc)
                    {
                        SamplePipProcessMetrics();
                        nextPipSampleUtc = DateTime.UtcNow.AddSeconds(1);
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await TerminateSetupProcessTreeAsync(process, job);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (DateTime.UtcNow >= deadline)
                    {
                        await TerminateSetupProcessTreeAsync(process, job);
                        throw new TimeoutException("Pythonセットアップが1時間でタイムアウトしました。出力が止まっていても、その時間までは処理を継続します。");
                    }
                    await Task.Delay(200);
                }
                await Task.Run(() => process.WaitForExit());
                SamplePipProcessMetrics();
                return process.ExitCode;
            }
            finally
            {
                lock (environmentSetupLock)
                {
                    if (ReferenceEquals(environmentSetupProcess, process))
                        environmentSetupProcess = null;
                }
                process.Dispose();
            }
        }

        private static async Task<int> RunPipInstallProcessAsync(string pythonPath, string requirementsPath,
            string backendDirectory, string setupRoot, string pipLogPath, string progressMode,
            SetupProcessJob job, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(pythonPath)) throw new FileNotFoundException("新しいvenvのPythonが見つかりません。", pythonPath);
            if (!File.Exists(requirementsPath)) throw new FileNotFoundException("requirements.txtが見つかりません。", requirementsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(pipLogPath));
            string setupTempDirectory = Path.Combine(setupRoot, ".tmp");
            Directory.CreateDirectory(setupTempDirectory);

            string mode = string.Equals(progressMode, "raw", StringComparison.OrdinalIgnoreCase) ? "raw" : "on";
            lock (environmentSetupLock)
            {
                environmentSetupPipProgressMode = mode;
                environmentSetupPipState = "起動待ち";
                environmentSetupPipProcessAlive = false;
            }
            if (mode != "raw")
                AppendEnvironmentSetupLog("INFO", "現在のpipではraw進捗が使えないため、通常バー・verbose出力・プロセス状態・pip詳細ログで監視します。");

            var arguments = new List<string>
            {
                "-m", "pip", "install", "--disable-pip-version-check", "--no-input",
                "--progress-bar", mode, "--log", pipLogPath
            };
            if (mode != "raw") arguments.Add("--verbose");
            arguments.Add("-r");
            arguments.Add(requirementsPath);
            var commandLine = new StringBuilder();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0) commandLine.Append(' ');
                commandLine.Append(QuoteCommandLineArgument(arguments[i]));
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = commandLine.ToString(),
                WorkingDirectory = backendDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
            startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            startInfo.EnvironmentVariables["PIP_NO_INPUT"] = "1";
            startInfo.EnvironmentVariables["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            startInfo.EnvironmentVariables["MPLBACKEND"] = "Agg";
            startInfo.EnvironmentVariables["MPLCONFIGDIR"] = Path.Combine(setupRoot, "matplotlib-config");
            startInfo.EnvironmentVariables["TEMP"] = setupTempDirectory;
            startInfo.EnvironmentVariables["TMP"] = setupTempDirectory;
            startInfo.EnvironmentVariables["TMPDIR"] = setupTempDirectory;

            lock (environmentSetupLock) environmentSetupProcess = null;
            DateTime nextSampleUtc = DateTime.UtcNow;
            try
            {
                int exitCode = await PythonChildProcessRunner.RunAsync(
                    startInfo,
                    cancellationToken,
                    (streamName, line) => AppendEnvironmentSetupOutput(streamName, line),
                    async process =>
                    {
                        lock (environmentSetupLock) environmentSetupProcess = process;
                        if (job != null)
                        {
                            try { job.Assign(process); }
                            catch
                            {
                                await TerminateSetupProcessTreeAsync(process, null);
                                throw;
                            }
                        }

                        lock (environmentSetupLock)
                        {
                            environmentSetupPipProcessId = process.Id;
                            environmentSetupPipProcessAlive = true;
                            environmentSetupPipState = "実行中";
                            environmentSetupLastPipSampleUtc = default(DateTime);
                            environmentSetupPipCpuSeconds = 0d;
                            environmentSetupPipPreviousReadBytes = 0;
                            environmentSetupPipPreviousWriteBytes = 0;
                        }
                        AppendEnvironmentSetupLog("INFO", "pipを直接起動しました。PID=" +
                            process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    },
                    () =>
                    {
                        if (DateTime.UtcNow < nextSampleUtc) return;
                        SamplePipProcessMetrics();
                        nextSampleUtc = DateTime.UtcNow.AddSeconds(1d);
                    },
                    process => TerminateSetupProcessTreeAsync(process, job),
                    TimeSpan.FromHours(1d),
                    "pipの依存ライブラリ導入が1時間でタイムアウトしました。pip詳細ログを確認してください。");

                SamplePipProcessMetrics();
                lock (environmentSetupLock)
                {
                    environmentSetupPipProcessAlive = false;
                    environmentSetupPipState = "終了 (ExitCode " +
                        exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                }
                AppendEnvironmentSetupLog(exitCode == 0 ? "INFO" : "ERROR",
                    "pip終了コード: " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return exitCode;
            }
            finally
            {
                lock (environmentSetupLock)
                {
                    environmentSetupProcess = null;
                    if (environmentSetupPipProcessAlive)
                    {
                        environmentSetupPipProcessAlive = false;
                        if (environmentSetupPipState == "実行中") environmentSetupPipState = "終了コード未取得";
                    }
                }
            }
        }

        private static async Task ReadEnvironmentSetupStreamAsync(StreamReader reader, string streamName)
        {
            await PythonChildProcessRunner.ReadStreamAsync(reader, streamName,
                (name, line) => AppendEnvironmentSetupOutput(name, line));
        }

        private static Task TerminateSetupProcessTreeAsync(Process process, SetupProcessJob job)
        {
            return PythonChildProcessRunner.TerminateProcessTreeAsync(process,
                job == null ? (Func<bool>)null : () => job.Terminate(1));
        }

        internal static bool TryParsePipProgressLine(string line, out long currentBytes, out long totalBytes)
        {
            currentBytes = 0;
            totalBytes = 0;
            if (string.IsNullOrWhiteSpace(line)) return false;
            string[] parts = Regex.Split(line.Trim(), @"\s+");
            return parts.Length == 4 && parts[0] == "Progress" && parts[2] == "of" &&
                long.TryParse(parts[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out currentBytes) &&
                long.TryParse(parts[3], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out totalBytes) &&
                currentBytes >= 0 && totalBytes >= 0;
        }

        private static void AppendEnvironmentSetupOutput(string streamName, string line)
        {
            line = line ?? string.Empty;
            RecordEnvironmentSetupOutputActivity(streamName);

            if (line == "PCWB_SETUP_PREPARED")
            {
                lock (environmentSetupLock) environmentSetupPreparedOnly = true;
                AppendEnvironmentSetupLog("INFO", "venv準備完了。pipはC#から直接起動します。");
                return;
            }

            const string stagePrefix = "PCWB_STAGE:";
            if (line.StartsWith(stagePrefix, StringComparison.Ordinal))
            {
                string stage = line.Substring(stagePrefix.Length).Trim();
                if (stage == "PYTHON_CHECK") SetEnvironmentSetupStage(0, environmentSetupStages[0]);
                else if (stage == "VENV_CREATE") SetEnvironmentSetupStage(1, environmentSetupStages[1]);
                else if (stage == "DEPENDENCIES") SetEnvironmentSetupStage(2, environmentSetupStages[2]);
                else if (stage == "VERIFY") SetEnvironmentSetupStage(3, environmentSetupStages[3]);
                AppendEnvironmentSetupLog(streamName, line);
                return;
            }

            const string pipPidPrefix = "PCWB_PIP_PID:";
            if (line.StartsWith(pipPidPrefix, StringComparison.Ordinal))
            {
                int processId;
                if (int.TryParse(line.Substring(pipPidPrefix.Length).Trim(), out processId) && processId > 0)
                {
                    lock (environmentSetupLock)
                    {
                        environmentSetupPipProcessId = processId;
                        environmentSetupPipProcessAlive = true;
                        environmentSetupPipState = "実行中";
                        environmentSetupLastPipSampleUtc = default(DateTime);
                    }
                }
                AppendEnvironmentSetupLog(streamName, line);
                return;
            }

            const string modePrefix = "PCWB_PIP_PROGRESS_MODE:";
            if (line.StartsWith(modePrefix, StringComparison.Ordinal))
            {
                lock (environmentSetupLock) environmentSetupPipProgressMode = line.Substring(modePrefix.Length).Trim();
                AppendEnvironmentSetupLog(streamName, line);
                return;
            }

            const string notePrefix = "PCWB_PIP_PROGRESS_NOTE:";
            if (line.StartsWith(notePrefix, StringComparison.Ordinal))
            {
                lock (environmentSetupLock) environmentSetupPipProgressNote = line.Substring(notePrefix.Length).Trim();
                AppendEnvironmentSetupLog(streamName, line.Substring(notePrefix.Length).Trim());
                return;
            }

            const string logPrefix = "PCWB_PIP_LOG:";
            if (line.StartsWith(logPrefix, StringComparison.Ordinal))
            {
                lock (environmentSetupLock) environmentSetupPipLogPath = line.Substring(logPrefix.Length).Trim();
                AppendEnvironmentSetupLog(streamName, line);
                return;
            }

            const string exitPrefix = "PCWB_PIP_EXIT:";
            if (line.StartsWith(exitPrefix, StringComparison.Ordinal))
            {
                int exitCode;
                lock (environmentSetupLock)
                {
                    environmentSetupPipProcessAlive = false;
                    environmentSetupPipState = int.TryParse(line.Substring(exitPrefix.Length).Trim(), out exitCode)
                        ? "終了 (ExitCode " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
                        : "終了コード不明";
                }
                AppendEnvironmentSetupLog(streamName, line);
                return;
            }

            long currentBytes;
            long totalBytes;
            if (TryParsePipProgressLine(line, out currentBytes, out totalBytes))
            {
                bool shouldLog;
                DateTime now = DateTime.UtcNow;
                lock (environmentSetupLock)
                {
                    environmentSetupPipProgressAvailable = true;
                    environmentSetupPipDownloadedBytes = currentBytes;
                    environmentSetupPipTotalBytes = totalBytes;
                    environmentSetupLastProgressUtc = now;
                    shouldLog = environmentSetupLastProgressLogUtc == default(DateTime) ||
                        (now - environmentSetupLastProgressLogUtc).TotalSeconds >= 1d ||
                        (totalBytes > 0 && currentBytes >= totalBytes);
                    if (shouldLog) environmentSetupLastProgressLogUtc = now;
                }
                if (shouldLog)
                    AppendEnvironmentSetupLog("PIP", "Progress " + currentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " of " + totalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            string package = ExtractPipPackage(line);
            if (!string.IsNullOrEmpty(package))
            {
                lock (environmentSetupLock)
                {
                    environmentSetupPipPackage = package;
                    environmentSetupPipProgressAvailable = false;
                    environmentSetupPipDownloadedBytes = 0;
                    environmentSetupPipTotalBytes = 0;
                }
            }

            if (IsVisualProgressLine(line)) return;
            AppendEnvironmentSetupLog(streamName, line);
        }

        private static void RecordEnvironmentSetupOutputActivity(string streamName)
        {
            DateTime now = DateTime.UtcNow;
            lock (environmentSetupLock)
            {
                environmentSetupLastOutputUtc = now;
                if (streamName == "OUT") environmentSetupLastStdoutUtc = now;
                else if (streamName == "ERR") environmentSetupLastStderrUtc = now;
            }
        }

        private static string ExtractPipPackage(string line)
        {
            string[] prefixes = { "Collecting ", "Downloading ", "Using cached ", "Installing collected packages:" };
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (!line.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) continue;
                string value = line.Substring(prefixes[i].Length).Trim();
                int whitespace = value.IndexOfAny(new[] { ' ', '\t', '(' });
                if (whitespace >= 0) value = value.Substring(0, whitespace);
                if (value.Length > 96) value = value.Substring(0, 93) + "...";
                return value;
            }
            return null;
        }

        private static bool IsVisualProgressLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (line.IndexOf('\u001b') >= 0) return true;
            string trimmed = line.Trim();
            if (trimmed.Length < 12) return false;
            int barCharacters = 0;
            for (int i = 0; i < trimmed.Length; i++)
                if (trimmed[i] == '-' || trimmed[i] == '=' || trimmed[i] == ' ' || trimmed[i] == '\r') barCharacters++;
            return barCharacters >= trimmed.Length * 9 / 10;
        }

        private static void SamplePipProcessMetrics()
        {
            int processId;
            lock (environmentSetupLock) processId = environmentSetupPipProcessId;
            if (processId <= 0) return;

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                    {
                        lock (environmentSetupLock)
                        {
                            environmentSetupPipProcessAlive = false;
                            if (environmentSetupPipState == "実行中") environmentSetupPipState = "終了コード受信待ち";
                        }
                        return;
                    }

                    double cpuSeconds = process.TotalProcessorTime.TotalSeconds;
                    long readBytes;
                    long writeBytes;
                    bool ioAvailable = SetupProcessJob.TryGetProcessIoCounters(processId, out readBytes, out writeBytes);
                    DateTime now = DateTime.UtcNow;
                    lock (environmentSetupLock)
                    {
                        environmentSetupPipCpuSecondsDelta = environmentSetupLastPipSampleUtc == default(DateTime)
                            ? 0d : Math.Max(0d, cpuSeconds - environmentSetupPipCpuSeconds);
                        environmentSetupPipCpuSeconds = cpuSeconds;
                        environmentSetupPipCpuAvailable = true;
                        if (ioAvailable)
                        {
                            environmentSetupPipReadBytesDelta = environmentSetupLastPipSampleUtc == default(DateTime)
                                ? 0 : Math.Max(0L, readBytes - environmentSetupPipPreviousReadBytes);
                            environmentSetupPipWriteBytesDelta = environmentSetupLastPipSampleUtc == default(DateTime)
                                ? 0 : Math.Max(0L, writeBytes - environmentSetupPipPreviousWriteBytes);
                            environmentSetupPipPreviousReadBytes = readBytes;
                            environmentSetupPipPreviousWriteBytes = writeBytes;
                            environmentSetupPipIoAvailable = true;
                        }
                        else
                        {
                            environmentSetupPipReadBytesDelta = 0;
                            environmentSetupPipWriteBytesDelta = 0;
                            environmentSetupPipIoAvailable = false;
                        }
                        environmentSetupPipProcessAlive = true;
                        environmentSetupPipState = "実行中";
                        environmentSetupLastPipSampleUtc = now;
                    }
                }
            }
            catch (ArgumentException)
            {
                lock (environmentSetupLock)
                {
                    environmentSetupPipProcessAlive = false;
                    if (environmentSetupPipState == "実行中") environmentSetupPipState = "PIDを確認できません";
                    environmentSetupPipCpuAvailable = false;
                    environmentSetupPipIoAvailable = false;
                }
            }
            catch (Exception ex)
            {
                lock (environmentSetupLock)
                {
                    environmentSetupPipState = "状態取得不可: " + ex.GetType().Name;
                    environmentSetupPipCpuAvailable = false;
                    environmentSetupPipIoAvailable = false;
                }
            }
        }

        private static void SetEnvironmentSetupStage(int index, string stage)
        {
            lock (environmentSetupLock)
            {
                environmentSetupStageIndex = Math.Max(0, Math.Min(environmentSetupStages.Length - 1, index));
                environmentSetupStage = stage;
            }
        }

        private static void AppendEnvironmentSetupLog(string streamName, string line)
        {
            line = RedactSensitiveText(line ?? string.Empty);
            string timestamped = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
                " [" + streamName + "] " + line;
            lock (environmentSetupLock)
            {
                environmentSetupTail.AppendLine(timestamped);
                environmentSetupRevision++;
                try
                {
                    if (environmentSetupLogWriter == null) return;
                    bool important = streamName == "ERROR" || streamName == "WARN" || streamName == "CANCEL";
                    const long maximumEventLogBytes = 16L * 1024L * 1024L;
                    if (environmentSetupLogWriter.BaseStream.Length < maximumEventLogBytes || important)
                    {
                        environmentSetupLogWriter.WriteLine(timestamped);
                    }
                    else if (!environmentSetupLogLimitNotified)
                    {
                        environmentSetupLogLimitNotified = true;
                        environmentSetupLogWriter.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff",
                            System.Globalization.CultureInfo.InvariantCulture) +
                            " [WARN] セットアップイベントログが16 MiBに達しました。以降の大量出力はpip詳細ログを参照してください。重要な失敗・警告は引き続き記録します。");
                    }
                }
                catch (Exception ex) { environmentSetupError = "セットアップログを書き込めません: " + ex.Message; }
            }
        }

        private static string RedactSensitiveText(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            value = Regex.Replace(value, @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[REDACTED]@");
            value = Regex.Replace(value, @"(?i)([?&](?:access[_-]?token|token|api[_-]?key|signature)=)[^&\s]+", "$1[REDACTED]");
            value = Regex.Replace(value, @"(?i)(authorization|password|passwd|token|api[_-]?key|access[_-]?key|secret)(\s*[=:]\s*)[^\s&]+", "$1$2[REDACTED]");
            return value;
        }

        private static void SanitizePipLogFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string temporaryPath = path + ".sanitized-" + Guid.NewGuid().ToString("N");
            string backupPath = path + ".backup-" + Guid.NewGuid().ToString("N");
            try
            {
                const int maximumBytes = 16 * 1024 * 1024;
                var lines = new Queue<string>();
                int retainedBytes = 0;
                bool wasTruncated = false;
                Encoding utf8 = new UTF8Encoding(false);
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = RedactSensitiveText(line);
                        string encodedLine = line + Environment.NewLine;
                        int lineBytes = utf8.GetByteCount(encodedLine);
                        if (lineBytes > maximumBytes)
                        {
                            int keepChars = Math.Min(line.Length, maximumBytes / 4);
                            line = line.Substring(line.Length - keepChars);
                            encodedLine = line + Environment.NewLine;
                            lineBytes = utf8.GetByteCount(encodedLine);
                            wasTruncated = true;
                        }
                        lines.Enqueue(encodedLine);
                        retainedBytes += lineBytes;
                        while (retainedBytes > maximumBytes && lines.Count > 1)
                        {
                            retainedBytes -= utf8.GetByteCount(lines.Dequeue());
                            wasTruncated = true;
                        }
                    }
                }
                using (var writer = new StreamWriter(temporaryPath, false, utf8))
                {
                    if (wasTruncated)
                        writer.WriteLine("[PCWB] pip log truncated to the most recent 16 MiB; older output is omitted.");
                    while (lines.Count > 0) writer.Write(lines.Dequeue());
                    writer.Flush();
                }
                File.Replace(temporaryPath, path, backupPath);
                File.Delete(backupPath);
            }
            catch (Exception ex)
            {
                AppendEnvironmentSetupLog("WARN", "pip詳細ログの秘匿情報処理またはサイズ制限に失敗しました: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception ex) { AppendEnvironmentSetupLog("WARN", "pip詳細ログの一時ファイルを削除できません: " + ex.Message); }
                try { if (File.Exists(backupPath)) File.Delete(backupPath); }
                catch (Exception ex) { AppendEnvironmentSetupLog("WARN", "pip詳細ログのバックアップ一時ファイルを削除できません: " + ex.Message); }
            }
        }

        private static string PruneEnvironmentSetupLogs(string logDirectory)
        {
            const int retainPerKind = 10;
            DateTime cutoffUtc = DateTime.UtcNow.AddDays(-90d);
            int removedFiles = 0;
            long removedBytes = 0;
            int failedFiles = 0;
            string[] patterns = { "python-setup-*.log", "pip-setup-*.log" };
            for (int patternIndex = 0; patternIndex < patterns.Length; patternIndex++)
            {
                FileInfo[] files;
                try
                {
                    files = new DirectoryInfo(logDirectory).GetFiles(patterns[patternIndex]);
                    Array.Sort(files, (left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
                }
                catch (Exception ex)
                {
                    return "セットアップログ保持処理を実行できません: " + ex.Message;
                }

                for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
                {
                    FileInfo file = files[fileIndex];
                    bool expired = file.LastWriteTimeUtc < cutoffUtc;
                    bool beyondRecentLimit = fileIndex >= retainPerKind &&
                        file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24d);
                    if (!expired && !beyondRecentLimit) continue;
                    try
                    {
                        long size = file.Length;
                        file.Delete();
                        removedFiles++;
                        removedBytes += size;
                    }
                    catch (Exception) { failedFiles++; }
                }
            }

            return "セットアップログ保持: 各種直近10件と直近24時間分を保持、90日超を整理。" +
                "削除=" + removedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "件/" + FormatByteCount(removedBytes) +
                (failedFiles > 0 ? "、削除失敗=" + failedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture) + "件" : string.Empty);
        }

        private static string FormatByteCount(long bytes)
        {
            return (bytes / (1024d * 1024d)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MiB";
        }

        private static string GetCachedEnvironmentSetupTail()
        {
            if (environmentSetupCachedRevision != environmentSetupRevision)
            {
                environmentSetupCachedTail = environmentSetupTail.ToString();
                environmentSetupCachedRevision = environmentSetupRevision;
            }
            return environmentSetupCachedTail;
        }

        private static void WriteActivatedEnvironmentManifest(string pythonPath, string environmentRoot,
            string requirementsPath)
        {
            string manifestPath = Path.Combine(environmentRoot, "active.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            string requirementsHash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(requirementsPath))
                requirementsHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            PythonEnvironmentManifest manifest = new PythonEnvironmentManifest
            {
                python_executable = Path.GetFullPath(pythonPath),
                requirements_sha256 = requirementsHash,
                validated_utc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            };
            string temporaryPath = manifestPath + ".tmp." + Guid.NewGuid().ToString("N");
            string manifestJson = "{\n" +
                "  \"python_executable\": \"" + EscapeJsonString(manifest.python_executable) + "\",\n" +
                "  \"requirements_sha256\": \"" + EscapeJsonString(manifest.requirements_sha256) + "\",\n" +
                "  \"validated_utc\": \"" + EscapeJsonString(manifest.validated_utc) + "\"\n" +
                "}\n";
            File.WriteAllText(temporaryPath, manifestJson, new UTF8Encoding(false));
            try
            {
                if (File.Exists(manifestPath)) File.Replace(temporaryPath, manifestPath, null);
                else File.Move(temporaryPath, manifestPath);
            }
            catch
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
                throw;
            }
        }

        private static string EscapeJsonString(string value)
        {
            StringBuilder escaped = new StringBuilder((value ?? string.Empty).Length + 8);
            foreach (char character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                            escaped.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        else escaped.Append(character);
                        break;
                }
            }
            return escaped.ToString();
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

            if (points == null || points.Length == 0)
                throw new InvalidDataException("ノイズ解析対象の点群が空です。");

            bool needsSelectionMask = HasEnabledSelectedComponentKeep(filterParams);
            byte[] deletedMaskSnapshot = new byte[points.Length];
            byte[] selectionMaskSnapshot = needsSelectionMask ? new byte[points.Length] : null;
            string expectedXyzSha256 = SnapshotPointInputs(points, deletedMaskSnapshot, selectionMaskSnapshot);

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
            string selectionMaskPath = needsSelectionMask
                ? Path.Combine(outputDir, "selection_mask." + operationId + ".bin")
                : null;
            string configJsonPath = Path.Combine(outputDir, "pipeline_config." + operationId + ".json");
            try
            {
            await WriteMaskSnapshotAsync(deletedMaskPath, deletedMaskSnapshot, cancellationToken);
            if (selectionMaskSnapshot != null)
                await WriteMaskSnapshotAsync(selectionMaskPath, selectionMaskSnapshot, cancellationToken);

            // 引数の構築
            string arguments = BuildArguments(scriptPath, inputPlyPath, outputDir, filterParams,
                deletedMaskPath, selectionMaskPath, expectedXyzSha256, configJsonPath,
                coordinateScaleToMm, operationId);

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
                TryDeleteTemporaryFile(selectionMaskPath);
                TryDeleteTemporaryFile(configJsonPath);
            }
        }

        private static bool HasEnabledSelectedComponentKeep(NoiseFilterParams parameters)
        {
            if (parameters == null) return false;
            var pipeline = parameters.GetPipeline();
            for (int i = 0; pipeline != null && i < pipeline.Count; i++)
            {
                if (pipeline[i] is ComponentKeepConfig component && component.enabled && component.mode == "selected")
                    return true;
            }
            return false;
        }

        private static string SnapshotPointInputs(PointData[] points, byte[] deletedMask, byte[] selectionMask)
        {
            const int pointsPerChunk = 16384;
            float[] coordinateChunk = new float[pointsPerChunk * 3];
            byte[] byteChunk = new byte[coordinateChunk.Length * sizeof(float)];
            using (SHA256 sha = SHA256.Create())
            {
                int cursor = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    int label = points[i].label;
                    deletedMask[i] = (label & (0x20000 | NoiseFilterManager.NOISE_HIDDEN_BIT)) != 0 ? (byte)1 : (byte)0;
                    if (selectionMask != null)
                        selectionMask[i] = (label & 0x10000) != 0 ? (byte)1 : (byte)0;

                    Vector3 position = points[i].position;
                    coordinateChunk[cursor++] = position.x;
                    coordinateChunk[cursor++] = position.y;
                    coordinateChunk[cursor++] = position.z;
                    if (cursor == coordinateChunk.Length || i == points.Length - 1)
                    {
                        int byteCount = cursor * sizeof(float);
                        Buffer.BlockCopy(coordinateChunk, 0, byteChunk, 0, byteCount);
                        if (!BitConverter.IsLittleEndian)
                        {
                            for (int offset = 0; offset < byteCount; offset += sizeof(float))
                                Array.Reverse(byteChunk, offset, sizeof(float));
                        }
                        sha.TransformBlock(byteChunk, 0, byteCount, byteChunk, 0);
                        cursor = 0;
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static async Task WriteMaskSnapshotAsync(string path, byte[] mask, CancellationToken cancellationToken)
        {
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await fs.WriteAsync(mask, 0, mask.Length, cancellationToken);
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
            await EnsureEnvironmentReadyAsync(cancellationToken, operation);
            cancellationToken.ThrowIfCancellationRequested();
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
            string deletedMaskPath, string selectionMaskPath, string expectedXyzSha256,
            string configJsonPath, float coordinateScaleToMm, string operationId)
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
                else if (step is ComponentKeepConfig component)
                {
                    jsonBuilder.Append($"        \"knn_k\": {component.knnK},\n");
                    jsonBuilder.Append($"        \"alpha\": {component.alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    string mode = component.mode == "selected" ? "selected" : "largest";
                    jsonBuilder.Append($"        \"mode\": \"{mode}\"\n");
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
            UnityEngine.Debug.Log($"[PythonBridge] ノイズ解析パイプライン設定: {jsonBuilder}");

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
            if (!string.IsNullOrEmpty(selectionMaskPath) && File.Exists(selectionMaskPath))
                argsBuilder.Append(" --selection_mask " + QuoteCommandLineArgument(selectionMaskPath));
            if (!string.IsNullOrEmpty(expectedXyzSha256))
                argsBuilder.Append(" --expected_xyz_sha256 " + QuoteCommandLineArgument(expectedXyzSha256));

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
