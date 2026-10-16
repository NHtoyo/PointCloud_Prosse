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
        TestSharedLockLogWriteReproducesSnapshotBlock();
        TestSetupLogSnapshotDoesNotWaitForDiskWrite();
        TestSetupLogTailIsBounded();
        TestSetupSnapshotRefreshIsThrottled();
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

    private static void TestSetupLogSnapshotDoesNotWaitForDiskWrite()
    {
        var log = new PythonEnvironmentSetupLog(1024);
        var stream = new BlockingWriteStream();
        log.SetWriter(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
        Task<string> append = Task.Run(() => log.AppendLine("blocked file write", false));
        try
        {
            Assert(stream.WaitForWrite(TimeSpan.FromSeconds(5)), "The log writer did not enter the blocked disk write.");
            string snapshotText = null;
            long revision = 0;
            Task snapshot = Task.Run(() => log.GetTail(out snapshotText, out revision));
            Assert(snapshot.Wait(TimeSpan.FromSeconds(1)),
                "The setup snapshot waited for a blocked file write.");
            Assert(snapshotText.Contains("blocked file write") && revision > 0,
                "The in-memory setup log was not available while disk output was blocked.");
        }
        finally
        {
            stream.ReleaseWrites();
            Assert(append.Wait(TimeSpan.FromSeconds(5)), "The log append did not finish after the disk write was released.");
            Assert(append.Result == null, "The log writer unexpectedly reported an error.");
            log.DisposeWriter();
        }
    }

    private static void TestSharedLockLogWriteReproducesSnapshotBlock()
    {
        object sharedLock = new object();
        var stream = new BlockingWriteStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        Task append = Task.Run(() => { lock (sharedLock) writer.WriteLine("blocked file write"); });
        Task<bool> snapshot = null;
        try
        {
            Assert(stream.WaitForWrite(TimeSpan.FromSeconds(5)), "The reproduction did not enter the blocked file write.");
            snapshot = Task.Run(() => { lock (sharedLock) return true; });
            Assert(!snapshot.Wait(TimeSpan.FromMilliseconds(100)),
                "The shared-lock reproduction did not block the snapshot behind file output.");
        }
        finally
        {
            stream.ReleaseWrites();
            Assert(append.Wait(TimeSpan.FromSeconds(5)), "The reproduction writer did not finish after release.");
            Assert(snapshot == null || snapshot.Wait(TimeSpan.FromSeconds(5)),
                "The reproduction snapshot did not resume after release.");
            writer.Dispose();
        }
    }

    private static void TestSetupLogTailIsBounded()
    {
        var log = new PythonEnvironmentSetupLog();
        for (int i = 0; i < 100; i++)
            log.AppendLine("line-" + i.ToString("D3") + "-" + new string('x', 80), false);

        log.GetTail(out string tail, out long revision);
        Assert(tail.Length <= PythonEnvironmentSetupLog.DefaultTailCapacity + 100,
            "The setup log tail exceeded its configured display bound.");
        Assert(tail.Contains("line-099-") && revision == 100,
            "The bounded setup log did not retain the latest lines and revision.");
    }

    private static void TestSetupSnapshotRefreshIsThrottled()
    {
        var cache = new ThrottledSnapshotCache<string>(TimeSpan.FromMilliseconds(100));
        Assert(!cache.TryGet(out string cached), "An empty setup snapshot cache returned a value.");
        cache.Store("setup snapshot");
        for (int i = 0; i < 1000; i++)
            Assert(cache.TryGet(out cached) && cached == "setup snapshot",
                "A frequent UI poll did not reuse the cached setup snapshot.");
        Thread.Sleep(150);
        Assert(!cache.TryGet(out cached), "The setup snapshot cache did not expire for a refresh.");
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

    private sealed class BlockingWriteStream : Stream
    {
        private readonly ManualResetEventSlim writeStarted = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim release = new ManualResetEventSlim(false);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public bool WaitForWrite(TimeSpan timeout) => writeStarted.Wait(timeout);
        public void ReleaseWrites() => release.Set();
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            writeStarted.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the blocked log stream.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                release.Set();
                writeStarted.Dispose();
                release.Dispose();
            }
            base.Dispose(disposing);
        }
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
