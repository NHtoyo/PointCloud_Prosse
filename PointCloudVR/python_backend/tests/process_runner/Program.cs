using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PointCloudWorkbench;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child") return RunChild(args);
        if (args.Length > 0 && (args[0] == "-B" || args[0] == "-m")) return RunFakePython(args);
        if (args.Length > 0 && args[0] == "--reproduce-job-gap")
            return ReproduceJobAssignmentGap().GetAwaiter().GetResult();
        try
        {
            RunUnitTests().GetAwaiter().GetResult();
            Console.WriteLine("PROCESS_RUNNER_TESTS:PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PROCESS_RUNNER_TESTS:FAIL\n" + ex);
            return 1;
        }
    }

    private static int RunFakePython(string[] args)
    {
        int offset = args[0] == "-B" ? 1 : 0;
        if (args.Length >= offset + 2 && args[offset] == "-c")
        {
            if (args[offset + 1].Contains("base=getattr"))
            {
                string path = Environment.ProcessPath.Replace("\\", "\\\\");
                Console.WriteLine("{\"version\":\"3.12.3\",\"base_executable\":\"" + path + "\"}");
            }
            return 0;
        }
        if (args.Length >= offset + 3 && args[offset] == "-m" && args[offset + 1] == "venv")
        {
            string scripts = Path.Combine(args[offset + 2], "Scripts");
            Directory.CreateDirectory(scripts);
            string target = Path.Combine(scripts, "python.exe");
            foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                string targetName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? "python.exe" : name;
                File.Copy(file, Path.Combine(scripts, targetName), true);
            }
            return File.Exists(target) ? 0 : 2;
        }
        if (args.Length >= offset + 4 && args[offset] == "-m" && args[offset + 1] == "pip" && args[offset + 2] == "install" &&
            Array.IndexOf(args, "--help") >= 0)
        {
            Console.WriteLine("--progress-bar {on,off,raw}");
            return 0;
        }
        if (args.Length >= offset + 3 && args[offset] == "-m" && args[offset + 1] == "pip" &&
            Array.IndexOf(args, "check") >= 0)
            return 0;
        if (args.Length >= offset + 3 && args[offset] == "-m" && args[offset + 1] == "pip" && args[offset + 2] == "install")
        {
            string logFlag = Array.IndexOf(args, "--log") >= 0
                ? args[Array.IndexOf(args, "--log") + 1] : null;
            if (!string.IsNullOrEmpty(logFlag))
                File.WriteAllText(logFlag, "fake pip detail\n", new UTF8Encoding(false));
            string mode = Environment.GetEnvironmentVariable("PCWB_FAKE_PIP_MODE") ?? "output";
            if (mode == "silent") Thread.Sleep(800);
            else
            {
                Console.Out.WriteLine("FAKE_PIP_STDOUT_日本語");
                Console.Out.Flush();
                Console.Error.WriteLine("FAKE_PIP_STDERR_日本語");
                Console.Error.Flush();
            }
            int exitCode;
            return int.TryParse(Environment.GetEnvironmentVariable("PCWB_FAKE_PIP_EXIT"), out exitCode)
                ? exitCode : 0;
        }
        return 0;
    }

    private static int RunChild(string[] args)
    {
        if (args.Length < 2) return 2;
        if (args[1] == "output")
        {
            Console.Out.Write("Progress 123 of 456\r");
            Console.Out.Flush();
            Console.Error.WriteLine("stderr-日本語");
            Console.Out.WriteLine("stdout-日本語");
            int exitCode;
            return args.Length > 2 && int.TryParse(args[2], out exitCode) ? exitCode : 0;
        }
        if (args[1] == "silent")
        {
            Thread.Sleep(800);
            Console.WriteLine("after-silence");
            return 0;
        }
        if (args[1] == "tree")
        {
            string pidPath = args[2];
            var info = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                Arguments = "--child sleep",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process child = Process.Start(info))
            {
                File.WriteAllText(pidPath, child.Id.ToString());
                Console.WriteLine("child-started=" + child.Id);
                Console.Out.Flush();
                Thread.Sleep(TimeSpan.FromSeconds(90));
            }
            return 0;
        }
        if (args[1] == "sleep")
        {
            Thread.Sleep(TimeSpan.FromSeconds(90));
            return 0;
        }
        return 2;
    }

    private static async Task RunUnitTests()
    {
        await TestOutputAndExitCode();
        await TestSilentProcessWaitsForExit();
        await TestCancellationKillsProcessTree();
    }

    private static async Task TestOutputAndExitCode()
    {
        var output = new ConcurrentQueue<string>();
        int exitCode = await PythonChildProcessRunner.RunAsync(
            ChildStartInfo("output 23"), CancellationToken.None,
            (stream, line) => output.Enqueue(stream + ":" + line), null, null,
            process => PythonChildProcessRunner.TerminateProcessTreeAsync(process, null),
            TimeSpan.FromSeconds(10), "test timeout");

        Assert(exitCode == 23, "The child exit code was not preserved.");
        string joined = string.Join("\n", output.ToArray());
        Assert(joined.Contains("OUT:Progress 123 of 456"), "Carriage-return progress output was lost.");
        Assert(joined.Contains("OUT:stdout-日本語"), "UTF-8 stdout was lost.");
        Assert(joined.Contains("ERR:stderr-日本語"), "UTF-8 stderr was lost.");
    }

    private static async Task TestSilentProcessWaitsForExit()
    {
        var output = new ConcurrentQueue<string>();
        Task<int> run = PythonChildProcessRunner.RunAsync(
            ChildStartInfo("silent"), CancellationToken.None,
            (stream, line) => output.Enqueue(stream + ":" + line), null, null,
            process => PythonChildProcessRunner.TerminateProcessTreeAsync(process, null),
            TimeSpan.FromSeconds(10), "test timeout");
        await Task.Delay(250);
        Assert(!run.IsCompleted, "A silent child was treated as finished before it exited.");
        Assert(output.IsEmpty, "The silent child unexpectedly produced output.");
        Assert(await run == 0, "The silent child did not return its exit code.");
        Assert(string.Join("\n", output.ToArray()).Contains("OUT:after-silence"),
            "Output after a silent interval was not received.");
    }

    private static async Task TestCancellationKillsProcessTree()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            throw new PlatformNotSupportedException("This process-tree test requires Windows.");
        string root = Path.Combine(Path.GetTempPath(), "pcwb-process-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pidPath = Path.Combine(root, "child.pid");
        using (var job = new TestJobObject())
        using (var cancellation = new CancellationTokenSource())
        {
            var output = new ConcurrentQueue<string>();
            Task<int> run = PythonChildProcessRunner.RunAsync(
                ChildStartInfo("tree \"" + pidPath + "\""), cancellation.Token,
                (stream, line) => output.Enqueue(stream + ":" + line),
                process => { job.Assign(process); return Task.CompletedTask; }, null,
                process => PythonChildProcessRunner.TerminateProcessTreeAsync(process, () => job.Terminate(1)),
                TimeSpan.FromSeconds(30), "test timeout");
            try
            {
                await WaitForFile(pidPath, TimeSpan.FromSeconds(10));
                int childPid = int.Parse(File.ReadAllText(pidPath));
                Assert(IsProcessRunning(childPid), "The child process did not start.");
                cancellation.Cancel();
                bool canceled = false;
                try { await run; }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled, "Cancellation was not propagated.");
                Assert(!IsProcessRunning(childPid), "A child process survived cancellation.");
            }
            finally
            {
                if (File.Exists(pidPath)) KillPidTree(int.Parse(File.ReadAllText(pidPath)));
                if (!run.IsCompleted) cancellation.Cancel();
                try { await run; } catch { }
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<int> ReproduceJobAssignmentGap()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcwb-job-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pidPath = Path.Combine(root, "child.pid");
        using (var parent = Process.Start(ChildStartInfo("tree \"" + pidPath + "\"")))
        using (var job = new TestJobObject())
        {
            try
            {
                await WaitForFile(pidPath, TimeSpan.FromSeconds(10));
                int childPid = int.Parse(File.ReadAllText(pidPath));
                job.Assign(parent);
                Assert(job.Terminate(1), "The old Job Object termination did not run.");
                Assert(parent.WaitForExit(10000), "The old parent process did not exit.");
                Assert(IsProcessRunning(childPid), "Expected to reproduce the escaped pre-assignment child.");
                Console.WriteLine("REPRODUCED: Job Object killed the assigned parent but left its earlier child alive.");
                return 0;
            }
            finally
            {
                if (File.Exists(pidPath)) KillPidTree(int.Parse(File.ReadAllText(pidPath)));
                if (!parent.HasExited) parent.Kill();
                await Task.Run(() => parent.WaitForExit(10000));
                Directory.Delete(root, true);
            }
        }
    }

    private static ProcessStartInfo ChildStartInfo(string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            Arguments = "--child " + arguments,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private static async Task WaitForFile(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert(File.Exists(path), "Timed out waiting for child PID file.");
    }

    private static bool IsProcessRunning(int pid)
    {
        try { using (Process process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void KillPidTree(int pid)
    {
        using (Process kill = Process.Start(new ProcessStartInfo
        {
            FileName = "taskkill.exe",
            Arguments = "/PID " + pid + " /T /F",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }))
        {
            if (kill == null) return;
            Task<string> stdout = kill.StandardOutput.ReadToEndAsync();
            Task<string> stderr = kill.StandardError.ReadToEndAsync();
            kill.WaitForExit(10000);
            Task.WaitAll(stdout, stderr);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestJobObject : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        private IntPtr handle;

        public TestJobObject() { handle = CreateJobObject(IntPtr.Zero, null); }
        public void Assign(Process process)
        {
            if (handle == IntPtr.Zero || !AssignProcessToJobObject(handle, process.Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public bool Terminate(uint exitCode) => handle != IntPtr.Zero && TerminateJobObject(handle, exitCode);
        public void Dispose()
        {
            IntPtr closing = handle;
            handle = IntPtr.Zero;
            if (closing != IntPtr.Zero) CloseHandle(closing);
        }
    }
}
