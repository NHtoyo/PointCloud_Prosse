using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

public sealed class PythonSetupProcessJobTests
{
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
