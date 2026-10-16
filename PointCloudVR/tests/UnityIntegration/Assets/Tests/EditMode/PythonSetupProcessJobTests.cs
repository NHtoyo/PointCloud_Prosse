using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

public sealed class PythonSetupProcessJobTests
{
    [TestCase("Progress 123 of 456", true, 123L, 456L)]
    [TestCase("Progress 987 of 0", true, 987L, 0L)]
    [TestCase("Progress 12 of 34\r", true, 12L, 34L)]
    [TestCase("Progress 12 of 34 日本語", false, 0L, 0L)]
    [TestCase("progress 12 of 34", false, 0L, 0L)]
    [TestCase("Progress x of 34", false, 0L, 0L)]
    public void PipProgressParserHandlesValidUnknownAndMalformedLines(
        string line, bool expectedValid, long expectedCurrent, long expectedTotal)
    {
        Type bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("PointCloudWorkbench.PythonBridge", false))
            .FirstOrDefault(type => type != null);
        Assert.That(bridgeType, Is.Not.Null);
        MethodInfo parser = bridgeType.GetMethod("TryParsePipProgressLine", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(parser, Is.Not.Null);

        object[] arguments = { line, 0L, 0L };
        bool actual = (bool)parser.Invoke(null, arguments);
        Assert.That(actual, Is.EqualTo(expectedValid));
        Assert.That((long)arguments[1], Is.EqualTo(expectedCurrent));
        Assert.That((long)arguments[2], Is.EqualTo(expectedTotal));
    }

    [Test]
    public void PipStreamReaderParsesCarriageReturnAndUnterminatedProgress()
    {
        Type bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("PointCloudWorkbench.PythonBridge", false))
            .FirstOrDefault(type => type != null);
        Assert.That(bridgeType, Is.Not.Null);
        MethodInfo readerMethod = bridgeType.GetMethod("ReadEnvironmentSetupStreamAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(readerMethod, Is.Not.Null);

        byte[] bytes = Encoding.UTF8.GetBytes("Collecting numpy\rProgress 123 of 456\rCollecting scipy\rProgress 7 of 0");
        using (var stream = new MemoryStream(bytes))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            Task readTask = (Task)readerMethod.Invoke(null, new object[] { reader, "OUT" });
            readTask.GetAwaiter().GetResult();
        }

        object snapshot = bridgeType.GetMethod("GetEnvironmentSetupSnapshot", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, null);
        Type snapshotType = snapshot.GetType();
        Assert.That((bool)snapshotType.GetField("pip_progress_available").GetValue(snapshot), Is.True);
        Assert.That((long)snapshotType.GetField("pip_downloaded_bytes").GetValue(snapshot), Is.EqualTo(7L));
        Assert.That((long)snapshotType.GetField("pip_total_bytes").GetValue(snapshot), Is.EqualTo(0L));
        Assert.That((string)snapshotType.GetField("pip_package").GetValue(snapshot), Is.EqualTo("scipy"));
    }

    [UnityTest]
    public IEnumerator DirectPipLaunchCapturesOutputAndExitStatus()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            Assert.Ignore("Windows process capture is required.");
        string pythonPath = Environment.GetEnvironmentVariable("PCWB_SETUP_TEST_VENV");
        string requirementsPath = Environment.GetEnvironmentVariable("PCWB_SETUP_TEST_REQUIREMENTS");
        string setupRoot = Environment.GetEnvironmentVariable("PCWB_SETUP_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(pythonPath) || string.IsNullOrWhiteSpace(requirementsPath) ||
            string.IsNullOrWhiteSpace(setupRoot))
            Assert.Ignore("Set PCWB_SETUP_TEST_VENV, PCWB_SETUP_TEST_REQUIREMENTS, and PCWB_SETUP_TEST_ROOT.");
        Assert.That(File.Exists(pythonPath), Is.True);
        Assert.That(File.Exists(requirementsPath), Is.True);
        Directory.CreateDirectory(setupRoot);

        Type bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("PointCloudWorkbench.PythonBridge", false))
            .FirstOrDefault(type => type != null);
        Assert.That(bridgeType, Is.Not.Null);
        Type jobType = bridgeType.GetNestedType("SetupProcessJob", BindingFlags.NonPublic);
        object job = jobType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        try
        {
            MethodInfo launch = bridgeType.GetMethod("RunPipInstallProcessAsync", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(launch, Is.Not.Null);
            string pipLogPath = Path.Combine(setupRoot, "pip-direct-test.log");
            Task<int> task = (Task<int>)launch.Invoke(null, new object[]
            {
                pythonPath,
                requirementsPath,
                Path.GetDirectoryName(requirementsPath),
                setupRoot,
                pipLogPath,
                "on",
                job,
                CancellationToken.None
            });
            while (!task.IsCompleted) yield return null;
            Assert.That(task.GetAwaiter().GetResult(), Is.EqualTo(0));
            Assert.That(File.Exists(pipLogPath), Is.True);
            Assert.That(new FileInfo(pipLogPath).Length, Is.GreaterThan(0));
            bridgeType.GetMethod("SanitizePipLogFile", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { pipLogPath });
            Assert.That(new FileInfo(pipLogPath).Length, Is.LessThanOrEqualTo(16L * 1024L * 1024L + 256L));
            Assert.That(Directory.GetFiles(setupRoot, "*.backup-*", SearchOption.TopDirectoryOnly), Is.Empty);

            object snapshot = bridgeType.GetMethod("GetEnvironmentSetupSnapshot", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, null);
            Type snapshotType = snapshot.GetType();
            int processId = (int)snapshotType.GetField("pip_process_id").GetValue(snapshot);
            string state = (string)snapshotType.GetField("pip_state").GetValue(snapshot);
            string log = (string)snapshotType.GetField("recent_log").GetValue(snapshot);
            Assert.That(processId, Is.GreaterThan(0));
            Assert.That(state, Does.Contain("ExitCode 0"));
            Assert.That(log, Does.Contain("pipを直接起動しました"));
            Assert.That(log, Does.Contain("Collecting idna").Or.Contain("Downloading idna").Or.Contain("Requirement already satisfied"));
            Assert.That(IsProcessRunning(processId), Is.False, "The pip process remained after completion.");
        }
        finally
        {
            jobType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(job, null);
        }
    }

    [Test]
    public void SilentSetupProcessRemainsObservableAndJobCancellationStopsIt()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            Assert.Ignore("Windows Job Objects are only available on Windows.");
        string pythonPath = Environment.GetEnvironmentVariable("PCWB_SETUP_TEST_VENV");
        if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
            Assert.Ignore("Set PCWB_SETUP_TEST_VENV to an isolated test interpreter.");

        Type bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("PointCloudWorkbench.PythonBridge", false))
            .FirstOrDefault(type => type != null);
        Assert.That(bridgeType, Is.Not.Null);
        Type jobType = bridgeType.GetNestedType("SetupProcessJob", BindingFlags.NonPublic);
        object job = jobType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        long stdoutLines = 0;
        long stderrLines = 0;
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = "-c \"import time; time.sleep(180)\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.OutputDataReceived += (_, args) => { if (args.Data != null) Interlocked.Increment(ref stdoutLines); };
            process.ErrorDataReceived += (_, args) => { if (args.Data != null) Interlocked.Increment(ref stderrLines); };
            try
            {
                Assert.That(process.Start(), Is.True);
                jobType.GetMethod("Assign", BindingFlags.Public | BindingFlags.Instance).Invoke(job, new object[] { process });
                int processId = process.Id;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                Thread.Sleep(1500);
                process.Refresh();
                Assert.That(process.HasExited, Is.False, "The silent test process exited before cancellation.");
                Assert.That(IsProcessRunning(processId), Is.True, "The recorded PID was not alive during the silent interval.");
                Assert.That(Interlocked.Read(ref stdoutLines), Is.EqualTo(0));
                Assert.That(Interlocked.Read(ref stderrLines), Is.EqualTo(0));

                jobType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(job, null);
                Assert.That(process.WaitForExit(10000), Is.True, "The silent process survived cancellation.");
                Assert.That(IsProcessRunning(processId), Is.False);
            }
            finally
            {
                jobType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(job, null);
                if (!process.HasExited) process.Kill();
            }
        }
    }

    [UnityTest]
    public IEnumerator Python312CandidateResolvesToTheBaseInterpreter()
    {
        string candidatePath = Environment.GetEnvironmentVariable("PCWB_SETUP_TEST_PYTHON");
        string expectedBasePath = Environment.GetEnvironmentVariable("PCWB_SETUP_TEST_BASE_PYTHON");
        string backendPath = Environment.GetEnvironmentVariable("PCWB_SETUP_TEST_BACKEND_PATH");
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(expectedBasePath) ||
            string.IsNullOrWhiteSpace(backendPath))
            Assert.Ignore("Set PCWB_SETUP_TEST_PYTHON, PCWB_SETUP_TEST_BASE_PYTHON, and PCWB_SETUP_TEST_BACKEND_PATH.");

        Type bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("PointCloudWorkbench.PythonBridge", false))
            .FirstOrDefault(type => type != null);
        Assert.That(bridgeType, Is.Not.Null);
        Type candidateType = bridgeType.GetNestedType("PythonCommandCandidate", BindingFlags.NonPublic);
        object candidate = Activator.CreateInstance(candidateType, new object[] { candidatePath, string.Empty });
        Type candidateListType = typeof(System.Collections.Generic.List<>).MakeGenericType(candidateType);
        object candidates = Activator.CreateInstance(candidateListType);
        candidateListType.GetMethod("Add").Invoke(candidates, new[] { candidate });

        MethodInfo findBasePython = bridgeType.GetMethod("FindPython312Async", BindingFlags.NonPublic | BindingFlags.Static);
        Task<string> task = (Task<string>)findBasePython.Invoke(null,
            new object[] { candidates, backendPath, CancellationToken.None });
        while (!task.IsCompleted)
            yield return null;
        Assert.That(task.GetAwaiter().GetResult(), Is.EqualTo(expectedBasePath));
    }

    [Test]
    public void DisposingPlayerOwnedJobTerminatesSetupProcessAndItsChild()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            Assert.Ignore("Windows Job Objects are only available on Windows.");

        Type bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("PointCloudWorkbench.PythonBridge", false))
            .FirstOrDefault(type => type != null);
        Assert.That(bridgeType, Is.Not.Null);
        Type jobType = bridgeType.GetNestedType("SetupProcessJob", BindingFlags.NonPublic);
        Assert.That(jobType, Is.Not.Null);
        object job = jobType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);

        string testRoot = Path.Combine(Path.GetTempPath(), "pcwb-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        string childPidPath = Path.Combine(testRoot, "child.pid");
        string scriptPath = Path.Combine(testRoot, "spawn-child.ps1");
        File.WriteAllText(scriptPath,
            "Start-Sleep -Seconds 1\n" +
            "$child = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\\WindowsPowerShell\\v1.0\\powershell.exe') " +
            "-ArgumentList '-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 90' " +
            "-PassThru -WindowStyle Hidden\n" +
            "[System.IO.File]::WriteAllText($env:PCWB_CHILD_PID_PATH, [string]$child.Id)\n" +
            "Start-Sleep -Seconds 90\n");

        Process process = null;
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32\\WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.EnvironmentVariables["PCWB_CHILD_PID_PATH"] = childPidPath;
            process = new Process { StartInfo = startInfo };
            Assert.That(process.Start(), Is.True);
            jobType.GetMethod("Assign", BindingFlags.Public | BindingFlags.Instance).Invoke(job, new object[] { process });

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(childPidPath) && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
            Assert.That(File.Exists(childPidPath), Is.True, "The setup child did not start.");
            int childPid = int.Parse(File.ReadAllText(childPidPath));
            Assert.That(IsProcessRunning(childPid), Is.True);

            jobType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(job, null);
            Assert.That(process.WaitForExit(10000), Is.True, "The setup parent survived job disposal.");
            Assert.That(IsProcessRunning(childPid), Is.False, "A setup descendant survived job disposal.");
        }
        finally
        {
            jobType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(job, null);
            if (process != null)
            {
                if (!process.HasExited) process.Kill();
                process.Dispose();
            }
            try { Directory.Delete(testRoot, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using (Process process = Process.GetProcessById(processId))
                return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
