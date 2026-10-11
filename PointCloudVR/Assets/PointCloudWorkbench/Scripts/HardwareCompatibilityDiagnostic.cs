using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    private string driverStatusText = "GPUドライバー情報を確認中...";
    private string saveStatus;
    private bool detailsOpen;
    private bool quitting;
    private Process activeProbe;
    private GUIStyle titleStyle;
    private GUIStyle bodyStyle;
    private GUIStyle warningStyle;
    private GUIStyle buttonStyle;

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

    private IEnumerator ProbePythonEnvironment()
    {
        string userPython = Path.Combine(Application.persistentDataPath, "PythonEnvironment", ".venv", "Scripts", "python.exe");
        string packagedPython = Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/.venv/Scripts/python.exe"));
        List<PythonCandidate> candidates = new List<PythonCandidate>();
        if (File.Exists(userPython)) candidates.Add(new PythonCandidate(userPython, string.Empty));
        if (File.Exists(packagedPython)) candidates.Add(new PythonCandidate(packagedPython, string.Empty));
        candidates.Add(new PythonCandidate("python", string.Empty));
        candidates.Add(new PythonCandidate("py", "-3.12 "));

        const string probe = "import importlib,importlib.util,importlib.metadata as m,json,sys\n" +
            "mods=[('numpy','numpy'),('scipy','scipy'),('open3d','open3d'),('fastapi','fastapi'),('uvicorn','uvicorn'),('pydantic','pydantic'),('matplotlib','matplotlib')]\n" +
            "expected={'open3d':'0.20.0','numpy':'2.5.3','scipy':'1.18.1','fastapi':'0.143.0','uvicorn':'0.54.0','pydantic':'2.14.0','matplotlib':'3.11.2'}\n" +
            "p=[]\n" +
            "for n,mod in mods:\n" +
            " try:\n  importlib.import_module(mod); p.append({'name':n,'version':m.version(n),'import_ok':True})\n" +
            " except Exception:\n  p.append({'name':n,'version':'missing','import_ok':False})\n" +
            "ok=sys.version_info[:2]==(3,12) and all(x['import_ok'] and x['version']==expected[x['name']] for x in p)\n" +
            "print(('PCWB_DIAG_OK:' if ok else 'PCWB_DIAG:')+json.dumps({'python_version':sys.version.split()[0],'packages':p}))\n";

        Task<string> probeTask = Task.Run(() => ProbePythonJson(candidates,
            Path.GetFullPath(Path.Combine(Application.dataPath, "..")), probe));
        yield return new WaitUntil(() => probeTask.IsCompleted);
        string output = null;
        try { output = probeTask.GetAwaiter().GetResult(); }
        catch { }
        string markerText = "PCWB_DIAG_OK:";
        int marker = output != null ? output.LastIndexOf(markerText, StringComparison.Ordinal) : -1;
        if (marker < 0)
        {
            markerText = "PCWB_DIAG:";
            marker = output != null ? output.LastIndexOf(markerText, StringComparison.Ordinal) : -1;
        }
        bool pythonEnvironmentAccepted = marker >= 0 && markerText == "PCWB_DIAG_OK:";
        bool probeParsed = false;
        if (marker >= 0)
        {
            try
            {
                string json = output.Substring(marker + markerText.Length).Trim();
                PythonProbeResult result = JsonUtility.FromJson<PythonProbeResult>(json);
                if (result != null)
                {
                    report.python_version = result.python_version;
                    report.python_packages = result.packages ?? new HardwarePackageReport[0];
                    bool required = result.python_version.StartsWith("3.12.", StringComparison.Ordinal);
                    for (int i = 0; i < report.python_packages.Length; i++)
                    {
                        HardwarePackageReport package = report.python_packages[i];
                        if (!PackageMatchesPinnedVersion(package)) required = false;
                    }
                    required &= pythonEnvironmentAccepted;
                    report.python_status = required ? "利用可能" : "必須ライブラリ不足またはバージョン不一致";
                    pythonStatusText = required
                        ? $"Python {report.python_version}: 固定バージョンの依存関係を確認しました。"
                        : $"Python {report.python_version}: 依存関係が不足または固定バージョンと異なります。解析実行時にセットアップを試みます。";
                    probeParsed = true;
                }
            }
            catch { }
        }
        if (probeParsed) yield break;

        report.python_status = "未検出または起動不可";
        report.python_version = "取得不可";
        report.python_packages = new HardwarePackageReport[0];
        pythonStatusText = "Python環境を確認できません。Python解析は利用できませんが、点群の閲覧・編集などは継続できます。";
    }

    private sealed class PythonCandidate
    {
        public readonly string FileName;
        public readonly string PrefixArguments;
        public PythonCandidate(string fileName, string prefixArguments)
        {
            FileName = fileName;
            PrefixArguments = prefixArguments;
        }
    }

    [Serializable]
    private sealed class PythonProbeResult
    {
        public string python_version;
        public HardwarePackageReport[] packages;
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

    private string ProbePythonJson(List<PythonCandidate> candidates, string workingDirectory, string probe)
    {
        string firstAvailableEnvironment = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            PythonCandidate candidate = candidates[i];
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = candidate.FileName,
                Arguments = candidate.PrefixArguments + "-c " + QuoteArgument(probe),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            string output = RunBoundedProcess(startInfo, 30000);
            if (!string.IsNullOrEmpty(output) && output.Contains("PCWB_DIAG_OK:")) return output;
            if (firstAvailableEnvironment == null && !string.IsNullOrEmpty(output) && output.Contains("PCWB_DIAG:"))
                firstAvailableEnvironment = output;
        }
        return firstAvailableEnvironment;
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
        Rect buttonRect = PointCloudUILayout.Calculate(Screen.width, Screen.height).DiagnosticButton;
        if (GUI.Button(buttonRect, "PC互換性・Python診断", buttonStyle)) detailsOpen = !detailsOpen;
        if (detailsOpen) DrawDetailsWindow();
    }

    private void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
        bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, richText = false };
        warningStyle = new GUIStyle(bodyStyle) { normal = { textColor = new Color(1f, 0.72f, 0.28f) } };
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, wordWrap = true, fixedHeight = 32 };
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
        float height = panel.height;
        GUI.Box(panel, GUIContent.none);
        GUILayout.BeginArea(new Rect(panel.x + 14f, panel.y + 12f, panel.width - 28f, panel.height - 24f));
        GUILayout.Label("PC互換性診断", titleStyle);
        GUILayout.Label(BuildSummary(), bodyStyle, GUILayout.Height(height - 118f));
        if (GUILayout.Button("診断JSONを保存", buttonStyle)) SaveReport();
        if (!string.IsNullOrEmpty(saveStatus)) GUILayout.Label(saveStatus, bodyStyle);
        if (GUILayout.Button("閉じる", buttonStyle)) detailsOpen = false;
        GUILayout.EndArea();
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
        if (report.python_packages != null)
            for (int i = 0; i < report.python_packages.Length; i++)
            {
                HardwarePackageReport package = report.python_packages[i];
                text.AppendLine($"  {package.name}: {package.version} ({(package.import_ok ? "import OK" : "import failed")})");
            }
        return text.ToString();
    }

    private static bool PackageMatchesPinnedVersion(HardwarePackageReport package)
    {
        if (!package.import_ok) return false;
        switch (package.name)
        {
            case "open3d": return package.version == "0.20.0";
            case "numpy": return package.version == "2.5.3";
            case "scipy": return package.version == "1.18.1";
            case "fastapi": return package.version == "0.143.0";
            case "uvicorn": return package.version == "0.54.0";
            case "pydantic": return package.version == "2.14.0";
            case "matplotlib": return package.version == "3.11.2";
            default: return false;
        }
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
