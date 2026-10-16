using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using PointCloudWorkbench;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

[Serializable]
public sealed class HardwareCompatibilityReport
{
    public string generated_utc;
    public string unity_version;
    public string operating_system;
    public string architecture;
    public string cpu;
    public int cpu_logical_processors;
    public int system_memory_mb;
    public string gpu;
    public string gpu_vendor;
    public string gpu_driver_versions;
    public string graphics_api;
    public int shader_level;
    public bool supports_compute_shaders;
    public int graphics_memory_capacity_mb;
    public int point_data_stride_bytes;
    public string required_shader;
    public bool required_shader_supported;
    public string python_status;
    public string python_version;
    public HardwarePackageReport[] python_packages;
}

[Serializable]
public sealed class HardwarePackageReport
{
    public string name;
    public string version;
    public bool import_ok;
}

public sealed class HardwareCompatibilityDiagnostic : MonoBehaviour
{
    private const string RequiredShaderName = "PointCloudWorkbench/PointCloudShader";
    private const string ReportFileName = "hardware_compatibility_report.json";
    private static HardwareCompatibilityDiagnostic instance;
    private static string registeredGraphicsFailure;

    private HardwareCompatibilityReport report;
    private string graphicsFailure;
    private string pythonStatusText = "Python環境を確認中...";
    private string pythonExecutableText = "未確認";
    private string driverStatusText = "GPUドライバー情報を確認中...";
    private string saveStatus;
    private Vector2 detailsScroll;
    private bool detailsOpen;
    private bool setupViewOpen;
    private bool setupConfirmationOpen;
    private bool setupCompletionRefreshed;
    private bool pythonProbeRunning;
    private string setupUiMessage;
    private Vector2 setupLogScroll;
    private bool quitting;
    private Process activeProbe;
    private GUIStyle titleStyle;
    private GUIStyle bodyStyle;
    private GUIStyle warningStyle;
    private GUIStyle buttonStyle;
    private GUIStyle setupLogStyle;

    public static bool HasBlockingGraphicsFailure => instance != null && !string.IsNullOrEmpty(instance.graphicsFailure);
    public static bool IsDetailsOpen => instance != null && instance.detailsOpen;

    public static bool BlocksUnderlyingInput(Vector2 guiPointer)
    {
        PointCloudUIRegions regions = PointCloudUILayout.Calculate(Screen.width, Screen.height);
        return PointCloudUILayout.BlocksUnderlyingInput(IsDetailsOpen, regions.DiagnosticButton, guiPointer);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<HardwareCompatibilityDiagnostic>() != null) return;
        GameObject host = new GameObject("HardwareCompatibilityDiagnostic");
        DontDestroyOnLoad(host);
        instance = host.AddComponent<HardwareCompatibilityDiagnostic>();
    }

    public static void ReportGraphicsFailure(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;
        if (instance == null) instance = FindAnyObjectByType<HardwareCompatibilityDiagnostic>();
        if (instance != null) instance.SetGraphicsFailure(reason);
        else Debug.LogError("[HardwareCompatibility] " + reason);
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
        CaptureHardwareReport();
        RefreshGraphicsStatus();
        StartCoroutine(ProbeEnvironments());
    }

    private void CaptureHardwareReport()
    {
        Shader shader = Shader.Find(RequiredShaderName);
        if (shader == null)
        {
            PointCloudRenderer renderer = FindAnyObjectByType<PointCloudRenderer>();
            if (renderer != null && renderer.pointShader != null && renderer.pointShader.name == RequiredShaderName)
                shader = renderer.pointShader;
        }
        int stride = 0;
        try { stride = System.Runtime.InteropServices.Marshal.SizeOf(typeof(PointCloudWorkbench.PointData)); }
        catch { }

        report = new HardwareCompatibilityReport
        {
            generated_utc = DateTime.UtcNow.ToString("o"),
            unity_version = Application.unityVersion,
            operating_system = SystemInfo.operatingSystem,
            architecture = (Environment.Is64BitOperatingSystem ? "64-bit OS" : "32-bit OS") +
                (Environment.Is64BitProcess ? " / 64-bit Player" : " / 32-bit Player"),
            cpu = SystemInfo.processorType,
            cpu_logical_processors = SystemInfo.processorCount,
            system_memory_mb = SystemInfo.systemMemorySize,
            gpu = SystemInfo.graphicsDeviceName,
            gpu_vendor = SystemInfo.graphicsDeviceVendor,
            gpu_driver_versions = "取得中",
            graphics_api = SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceVersion,
            shader_level = SystemInfo.graphicsShaderLevel,
            supports_compute_shaders = SystemInfo.supportsComputeShaders,
            graphics_memory_capacity_mb = SystemInfo.graphicsMemorySize,
            point_data_stride_bytes = stride,
            required_shader = RequiredShaderName,
            required_shader_supported = shader != null && shader.isSupported,
            python_status = "確認中",
            python_version = "未確認",
            python_packages = new HardwarePackageReport[0]
        };
    }

    private void RefreshGraphicsStatus()
    {
        string reason = EvaluateGraphicsCompatibility(SystemInfo.graphicsShaderLevel,
            SystemInfo.supportsComputeShaders, report.point_data_stride_bytes,
            report.required_shader_supported,
            SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null);
        List<string> failures = new List<string>();
        if (!string.IsNullOrEmpty(reason)) failures.Add(reason);
        if (!string.IsNullOrEmpty(registeredGraphicsFailure)) failures.Add(registeredGraphicsFailure);
        graphicsFailure = failures.Count == 0 ? string.Empty : string.Join("\n", failures.ToArray());
    }

    public static string EvaluateGraphicsCompatibility(int shaderLevel, bool supportsCompute,
        int pointDataStrideBytes, bool requiredShaderSupported, bool graphicsDeviceAvailable)
    {
        List<string> failures = new List<string>();
        // supportsCompute is reported separately; the renderer uses regular shaders with structured buffers, not ComputeShader dispatch.
        if (shaderLevel < 50) failures.Add($"Shader Model 5.0が必要ですが、graphicsShaderLevel={shaderLevel}です。");
        if (!graphicsDeviceAvailable) failures.Add("有効なGraphics API/描画デバイスがありません。");
        if (pointDataStrideBytes != 24)
            failures.Add($"PointDataのGPUデータ幅が不一致です ({pointDataStrideBytes} bytes、必要値24 bytes)。");
        if (!requiredShaderSupported) failures.Add("必須点群シェーダーが見つからないか、このGraphics APIで利用できません。");
        return string.Join("\n", failures.ToArray());
    }

    private void SetGraphicsFailure(string reason)
    {
        if (string.Equals(registeredGraphicsFailure, reason, StringComparison.Ordinal)) return;
        registeredGraphicsFailure = reason;
        RefreshGraphicsStatus();
        Debug.LogError("[HardwareCompatibility] 点群描画を開始できません: " + reason);
    }

    private IEnumerator ProbeDriverVersion()
    {
        Task<string> task = Task.Run(QueryGpuDriverVersions);
        yield return new WaitUntil(() => task.IsCompleted);
        string result = null;
        try { result = task.GetAwaiter().GetResult(); }
        catch { }
        result = string.IsNullOrWhiteSpace(result) ? null : result.Trim().Replace("\r", "");
        driverStatusText = result == null ? "GPUドライバー情報は取得できませんでした。" : "GPUドライバー: " + result;
        if (report != null) report.gpu_driver_versions = result ?? "取得不可";
    }

    private IEnumerator ProbeEnvironments()
    {
        yield return StartCoroutine(ProbeDriverVersion());
        if (!quitting) yield return StartCoroutine(ProbePythonEnvironment());
    }

    private void Update()
    {
        PythonEnvironmentSetupSnapshot snapshot = PythonBridge.GetEnvironmentSetupSnapshot();
        if (snapshot.is_complete && !setupCompletionRefreshed)
        {
            setupCompletionRefreshed = true;
            ApplyPythonEnvironmentInfo(PythonBridge.GetLastEnvironmentInfo());
        }
    }

    private IEnumerator ProbePythonEnvironment()
    {
        if (pythonProbeRunning) yield break;
        pythonProbeRunning = true;
        Task<PythonEnvironmentInfo> probeTask = PythonBridge.ValidateAvailableEnvironmentAsync(CancellationToken.None);
        yield return new WaitUntil(() => probeTask.IsCompleted);
        if (quitting)
        {
            pythonProbeRunning = false;
            yield break;
        }
        PythonEnvironmentInfo info = null;
        try { info = probeTask.GetAwaiter().GetResult(); }
        catch (Exception ex) { info = new PythonEnvironmentInfo { is_compatible = false, message = ex.Message }; }
        ApplyPythonEnvironmentInfo(info);
        pythonProbeRunning = false;
    }

    private void ApplyPythonEnvironmentInfo(PythonEnvironmentInfo info)
    {
        if (info == null)
        {
            report.python_status = "確認失敗";
            report.python_version = "取得不可";
            report.python_packages = new HardwarePackageReport[0];
            pythonExecutableText = "未確認";
            pythonStatusText = "Python環境を確認できません。点群の閲覧・編集は継続できます。";
            return;
        }

        report.python_version = string.IsNullOrWhiteSpace(info.python_version) ? "取得不可" : info.python_version;
        report.python_packages = new HardwarePackageReport[info.packages != null ? info.packages.Length : 0];
        for (int i = 0; i < report.python_packages.Length; i++)
        {
            PythonEnvironmentPackageInfo package = info.packages[i];
            report.python_packages[i] = new HardwarePackageReport
            {
                name = package.name,
                version = package.version,
                import_ok = package.import_ok
            };
        }
        pythonExecutableText = string.IsNullOrWhiteSpace(info.python_path) ? "未確認" : info.python_path;
        if (string.IsNullOrWhiteSpace(info.python_path))
        {
            report.python_status = "Python未検出";
            pythonStatusText = "Python 3.12を検出できません。Python本体を導入後、「Pythonを再検出」してください。";
        }
        else if (!report.python_version.StartsWith("3.12.", StringComparison.Ordinal))
        {
            report.python_status = "Pythonバージョン非対応";
            pythonStatusText = $"Python {report.python_version}を検出しましたが、Python 3.12.xが必要です。";
        }
        else if (!info.is_compatible)
        {
            report.python_status = "依存ライブラリまたはAPI不整合";
            pythonStatusText = "Python 3.12は検出しましたが、必要ライブラリ・バージョン・APIの検証に失敗しました: " +
                (info.message ?? "詳細を診断ログで確認してください。");
        }
        else
        {
            report.python_status = "利用可能";
            pythonStatusText = $"Python {report.python_version}: 依存ライブラリと主要APIを確認しました。";
        }
    }

    private static string QuoteArgument(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        int slashCount = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (current == '\\') { slashCount++; continue; }
            if (current == '"')
            {
                builder.Append('\\', slashCount * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', slashCount);
                builder.Append(current);
            }
            slashCount = 0;
        }
        builder.Append('\\', slashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private string QueryGpuDriverVersions()
    {
        const string command = "Get-CimInstance Win32_VideoController | ForEach-Object { \"$($_.Name)|$($_.DriverVersion)\" }";
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command " + QuoteArgument(command),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        return RunBoundedProcess(startInfo, 5000);
    }

    private string RunBoundedProcess(ProcessStartInfo startInfo, int timeoutMilliseconds)
    {
        if (quitting) return null;
        try
        {
            using (Process process = new Process { StartInfo = startInfo })
            {
                if (!process.Start()) return null;
                lock (this)
                {
                    activeProbe = process;
                    if (quitting) TryStopProbe(process);
                }
                if (process.HasExited) return null;
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    TryStopProbe(process);
                    return null;
                }
                Task.WaitAll(new Task[] { outputTask, errorTask }, 2000);
                if (!outputTask.IsCompleted || process.ExitCode != 0) return null;
                return outputTask.Result;
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (this) activeProbe = null;
        }
    }

    private void TryStopProbe(Process process)
    {
        try
        {
            if (process != null && !process.HasExited) process.Kill();
            if (process != null) process.WaitForExit(1500);
        }
        catch { }
    }

    private void OnApplicationQuit()
    {
        quitting = true;
        lock (this) TryStopProbe(activeProbe);
    }

    private void OnGUI()
    {
        EnsureStyles();
        if (!string.IsNullOrEmpty(graphicsFailure))
        {
            DrawBlockingGraphicsError();
            Event current = Event.current;
            if (current.type == EventType.MouseDown || current.type == EventType.MouseUp ||
                current.type == EventType.MouseDrag || current.type == EventType.ScrollWheel ||
                current.type == EventType.KeyDown || current.type == EventType.KeyUp ||
                current.type == EventType.MouseMove)
                current.Use();
            return;
        }

        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F10 && GUIUtility.keyboardControl == 0)
            detailsOpen = !detailsOpen;
        if (detailsOpen)
        {
            if (setupViewOpen) DrawPythonSetupWindow();
            else DrawDetailsWindow();
            return;
        }
        Rect buttonRect = PointCloudUILayout.Calculate(Screen.width, Screen.height).DiagnosticButton;
        if (GUI.Button(buttonRect, "PC互換性・Python診断", buttonStyle)) detailsOpen = !detailsOpen;
    }

    private void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
        bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, richText = false };
        warningStyle = new GUIStyle(bodyStyle) { normal = { textColor = new Color(1f, 0.72f, 0.28f) } };
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, wordWrap = true, fixedHeight = 32 };
        setupLogStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            wordWrap = true,
            alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(6, 6, 4, 4)
        };
    }

    private void DrawBlockingGraphicsError()
    {
        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.86f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
        float width = Mathf.Min(720f, Screen.width - 32f);
        float height = Mathf.Min(430f, Screen.height - 32f);
        Rect panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        GUI.Box(panel, GUIContent.none);
        GUILayout.BeginArea(new Rect(panel.x + 20f, panel.y + 18f, panel.width - 40f, panel.height - 36f));
        GUILayout.Label("点群を表示できないGPU/API構成です", titleStyle);
        GUILayout.Space(8f);
        GUILayout.Label(graphicsFailure, warningStyle);
        GUILayout.Label($"GPU: {report.gpu} | API: {report.graphics_api} | Shader level: {report.shader_level}", bodyStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("互換性レポートを保存", buttonStyle)) SaveReport();
        if (!string.IsNullOrEmpty(saveStatus)) GUILayout.Label(saveStatus, bodyStyle);
        if (GUILayout.Button("アプリを終了", buttonStyle)) Application.Quit();
        GUILayout.EndArea();
        GUI.color = previous;
    }

    private void DrawDetailsWindow()
    {
        Rect panel = PointCloudUILayout.Calculate(Screen.width, Screen.height).DiagnosticDetails;
        GUI.Box(panel, GUIContent.none);
        GUILayout.BeginArea(new Rect(panel.x + 14f, panel.y + 12f, panel.width - 28f, panel.height - 24f));
        GUILayout.Label("PC互換性診断", titleStyle);
        detailsScroll = GUILayout.BeginScrollView(detailsScroll, GUILayout.ExpandHeight(true));
        GUILayout.Label(BuildSummary(), bodyStyle);
        GUILayout.EndScrollView();
        if (pythonProbeRunning)
            GUILayout.Label("Python環境を再検出しています...", bodyStyle);
        else if (string.IsNullOrWhiteSpace(pythonExecutableText) || pythonExecutableText == "未確認")
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Python 3.12の公式案内", buttonStyle))
                Application.OpenURL("https://www.python.org/downloads/windows/");
            if (GUILayout.Button("Pythonを再検出", buttonStyle)) StartCoroutine(ProbePythonEnvironment());
            GUILayout.EndHorizontal();
        }
        else if (!string.Equals(report.python_status, "利用可能", StringComparison.Ordinal))
        {
            if (GUILayout.Button("Python環境を再検出", buttonStyle)) StartCoroutine(ProbePythonEnvironment());
        }
        if (GUILayout.Button("Python環境をセットアップ／修復", buttonStyle))
        {
            setupViewOpen = true;
            setupConfirmationOpen = false;
            setupUiMessage = null;
        }
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("診断JSONを保存", buttonStyle)) SaveReport();
        if (GUILayout.Button("閉じる", buttonStyle)) detailsOpen = false;
        GUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(saveStatus)) GUILayout.Label(saveStatus, bodyStyle);
        GUILayout.EndArea();
    }

    private void DrawPythonSetupWindow()
    {
        Rect panel = PointCloudUILayout.Calculate(Screen.width, Screen.height).DiagnosticDetails;
        GUI.Box(panel, GUIContent.none);
        GUILayout.BeginArea(new Rect(panel.x + 14f, panel.y + 12f, panel.width - 28f, panel.height - 24f));
        GUILayout.Label("Python環境セットアップ", titleStyle);
        PythonEnvironmentSetupSnapshot snapshot = PythonBridge.GetEnvironmentSetupSnapshot();

        if (setupConfirmationOpen && !snapshot.is_running)
        {
            GUILayout.Label(
                "新しい独立したPython環境を作成し、requirements.txtの依存関係を導入します。既存の正常な環境は更新・削除しません。インターネット接続と空きディスクが必要です。",
                bodyStyle, GUILayout.ExpandHeight(true));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("セットアップを開始", buttonStyle))
            {
                setupConfirmationOpen = false;
                setupCompletionRefreshed = false;
                setupUiMessage = null;
                PythonBridge.StartEnvironmentSetup();
            }
            if (GUILayout.Button("戻る", buttonStyle)) setupConfirmationOpen = false;
            GUILayout.EndHorizontal();
        }
        else
        {
            string state = snapshot.is_running ? "セットアップ中: " + snapshot.stage
                : snapshot.is_complete ? "完了: 新しい環境を検証し、有効化しました。"
                : snapshot.has_error ? "失敗: " + snapshot.stage
                : snapshot.was_cancelled ? "キャンセル済み: 既存環境は保持されています。"
                : "既存環境は保持されます。新規導入は利用者が開始を選んだ場合だけ行います。";
            GUILayout.Label(state, snapshot.has_error ? warningStyle : bodyStyle);

            if (snapshot.is_running || snapshot.is_complete || snapshot.has_error || snapshot.was_cancelled)
            {
                string[] stageNames = { "Python 3.12の確認", "仮想環境の作成", "依存ライブラリの導入", "動作検証" };
                for (int i = 0; i < stageNames.Length; i++)
                {
                    string marker = i < snapshot.stage_index ? "完了" :
                        i == snapshot.stage_index && snapshot.is_running ? "実行中" :
                        i == snapshot.stage_index && snapshot.was_cancelled ? "キャンセル" :
                        snapshot.has_error && i == snapshot.stage_index ? "失敗" : "待機中";
                    GUILayout.Label("[" + (i + 1) + "/4] " + stageNames[i] + "  " + marker, bodyStyle);
                }
                GUILayout.Label("経過時間: " + FormatDuration(snapshot.elapsed_seconds) +
                    "　最終ログ受信: " + snapshot.seconds_since_output.ToString("F0") + "秒前", bodyStyle);
                if (!string.IsNullOrEmpty(snapshot.python_path))
                    GUILayout.Label("Python: " + snapshot.python_path, bodyStyle);
                if (!string.IsNullOrEmpty(snapshot.setup_directory))
                    GUILayout.Label("セットアップ先: " + snapshot.setup_directory, bodyStyle);
                if (!string.IsNullOrEmpty(snapshot.log_path))
                    GUILayout.Label("ログ: " + snapshot.log_path, bodyStyle);
                if (snapshot.has_error) GUILayout.Label(FirstLine(snapshot.error), warningStyle);
            }

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true));
            setupLogScroll = GUILayout.BeginScrollView(setupLogScroll, GUILayout.ExpandHeight(true));
            GUILayout.Label(string.IsNullOrEmpty(snapshot.recent_log) ? "セットアップログはまだありません。" : snapshot.recent_log, setupLogStyle);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.BeginHorizontal();
            if (snapshot.is_running)
            {
                if (GUILayout.Button("処理をキャンセル", buttonStyle)) PythonBridge.CancelEnvironmentSetup();
            }
            else if (!snapshot.is_complete)
            {
                if (GUILayout.Button(snapshot.has_error || snapshot.was_cancelled ? "再試行" : "開始", buttonStyle))
                    setupConfirmationOpen = true;
            }
            if (!string.IsNullOrEmpty(snapshot.log_path))
            {
                if (GUILayout.Button("全文コピー", buttonStyle)) CopySetupLog(snapshot.log_path);
                if (GUILayout.Button("ログを開く", buttonStyle)) OpenSetupLog(snapshot.log_path);
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (snapshot.has_error && snapshot.error.Contains("Python 3.12が見つかりません"))
            {
                if (GUILayout.Button("Python 3.12の公式案内", buttonStyle))
                    Application.OpenURL("https://www.python.org/downloads/windows/");
            }
            if (GUILayout.Button("診断へ戻る", buttonStyle)) setupViewOpen = false;
            if (GUILayout.Button("閉じる", buttonStyle)) detailsOpen = false;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(setupUiMessage)) GUILayout.Label(setupUiMessage, bodyStyle);
        }
        GUILayout.EndArea();
    }

    private static string FormatDuration(double seconds)
    {
        TimeSpan value = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        return value.ToString(value.TotalHours >= 1d ? @"hh\:mm\:ss" : @"mm\:ss");
    }

    private static string FirstLine(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        int lineEnd = value.IndexOfAny(new[] { '\r', '\n' });
        return lineEnd < 0 ? value : value.Substring(0, lineEnd);
    }

    private void CopySetupLog(string path)
    {
        try
        {
            GUIUtility.systemCopyBuffer = File.ReadAllText(path, Encoding.UTF8);
            setupUiMessage = "ログ全文をクリップボードへコピーしました。";
        }
        catch (Exception ex) { setupUiMessage = "ログをコピーできません: " + ex.Message; }
    }

    private void OpenSetupLog(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            setupUiMessage = "ログファイルを開きました。";
        }
        catch (Exception ex) { setupUiMessage = "ログを開けません: " + ex.Message; }
    }

    private string BuildSummary()
    {
        StringBuilder text = new StringBuilder(512);
        text.AppendLine($"Windows: {report.operating_system} ({report.architecture})");
        text.AppendLine($"CPU: {report.cpu} / {report.cpu_logical_processors} logical processors / RAM {report.system_memory_mb} MB");
        text.AppendLine($"GPU: {report.gpu} ({report.gpu_vendor})");
        text.AppendLine(driverStatusText);
        text.AppendLine($"Graphics API: {report.graphics_api}");
        text.AppendLine($"Shader level: {report.shader_level} / Compute: {report.supports_compute_shaders}");
        text.AppendLine($"Reported graphics memory capacity: {report.graphics_memory_capacity_mb} MB (usage is not measured)");
        text.AppendLine($"Required shader: {(report.required_shader_supported ? "supported" : "unsupported")}; PointData stride: {report.point_data_stride_bytes} bytes");
        text.AppendLine(pythonStatusText);
        text.AppendLine("Python executable: " + pythonExecutableText);
        if (report.python_packages != null)
            for (int i = 0; i < report.python_packages.Length; i++)
            {
                HardwarePackageReport package = report.python_packages[i];
                text.AppendLine($"  {package.name}: {package.version} ({(package.import_ok ? "import OK" : "import failed")})");
            }
        return text.ToString();
    }

    private void SaveReport()
    {
        try
        {
            if (report == null) CaptureHardwareReport();
            report.generated_utc = DateTime.UtcNow.ToString("o");
            report.gpu_driver_versions = string.IsNullOrEmpty(report.gpu_driver_versions) ? "取得不可" : report.gpu_driver_versions;
            report.python_status = string.IsNullOrEmpty(report.python_status) ? "確認中" : report.python_status;
            string path = Path.Combine(Application.persistentDataPath, ReportFileName);
            File.WriteAllText(path, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            saveStatus = "保存しました: " + path;
        }
        catch (Exception ex)
        {
            saveStatus = "レポートを保存できませんでした: " + ex.Message;
        }
    }
}
