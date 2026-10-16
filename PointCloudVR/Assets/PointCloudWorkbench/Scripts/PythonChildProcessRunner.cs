using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PointCloudWorkbench
{
    public static class PythonChildProcessRunner
    {
        public static async Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken,
            Action<string, string> output, Func<Process, Task> onStarted, Action onPoll,
            Func<Process, Task> terminateProcessTree, TimeSpan timeout, string timeoutMessage)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
            if (terminateProcessTree == null) throw new ArgumentNullException(nameof(terminateProcessTree));
            cancellationToken.ThrowIfCancellationRequested();

            var process = new Process { StartInfo = startInfo };
            Task stdoutTask = null;
            Task stderrTask = null;
            bool started = false;
            try
            {
                if (!process.Start()) throw new InvalidOperationException("子プロセスを起動できませんでした。");
                started = true;
                stdoutTask = ReadStreamAsync(process.StandardOutput, "OUT", output);
                stderrTask = ReadStreamAsync(process.StandardError, "ERR", output);
                if (onStarted != null) await onStarted(process);

                DateTime deadline = DateTime.UtcNow + timeout;
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await terminateProcessTree(process);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (DateTime.UtcNow >= deadline)
                    {
                        await terminateProcessTree(process);
                        throw new TimeoutException(timeoutMessage);
                    }
                    if (onPoll != null) onPoll();
                    await Task.Delay(100);
                }

                await Task.Run(() => process.WaitForExit());
                await Task.WhenAll(stdoutTask, stderrTask);
                return process.ExitCode;
            }
            catch
            {
                if (started && !process.HasExited)
                    await terminateProcessTree(process);
                throw;
            }
            finally
            {
                process.Dispose();
                if (stdoutTask != null || stderrTask != null)
                {
                    try { await Task.WhenAll(stdoutTask ?? Task.FromResult(0), stderrTask ?? Task.FromResult(0)); }
                    catch (ObjectDisposedException) { }
                    catch (IOException) { }
                }
            }
        }

        internal static async Task ReadStreamAsync(StreamReader reader, string streamName,
            Action<string, string> output)
        {
            char[] buffer = new char[4096];
            var line = new System.Text.StringBuilder();
            bool skipLineFeed = false;
            while (true)
            {
                int count = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (count <= 0) break;
                for (int i = 0; i < count; i++)
                {
                    char character = buffer[i];
                    if (skipLineFeed)
                    {
                        skipLineFeed = false;
                        if (character == '\n') continue;
                    }
                    if (character == '\r' || character == '\n')
                    {
                        if (line.Length > 0)
                        {
                            if (output != null) output(streamName, line.ToString());
                            line.Length = 0;
                        }
                        skipLineFeed = character == '\r';
                        continue;
                    }

                    line.Append(character);
                    if (line.Length >= 64 * 1024)
                    {
                        if (output != null) output(streamName, line + " [長い出力行を分割]");
                        line.Length = 0;
                    }
                }
            }
            if (line.Length > 0 && output != null) output(streamName, line.ToString());
        }

        public static async Task TerminateProcessTreeAsync(Process process, Func<bool> terminateOwnedJob)
        {
            if (process == null) return;
            string treeKillError = null;
            bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

            // Kill the OS process tree before the Job Object. A child created just before
            // its parent was assigned to the job is not retroactively added to that job.
            if (isWindows && !process.HasExited)
            {
                try
                {
                    var killInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = "/PID " + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (Process killer = Process.Start(killInfo))
                    {
                        if (killer == null) throw new InvalidOperationException("taskkill.exeを起動できませんでした。");
                        Task<string> stdout = killer.StandardOutput.ReadToEndAsync();
                        Task<string> stderr = killer.StandardError.ReadToEndAsync();
                        if (!await Task.Run(() => killer.WaitForExit(10000)))
                        {
                            killer.Kill();
                            throw new TimeoutException("taskkill.exeが10秒以内に終了しませんでした。");
                        }
                        await Task.WhenAll(stdout, stderr);
                        if (killer.ExitCode != 0)
                            treeKillError = (stderr.Result + Environment.NewLine + stdout.Result).Trim();
                    }
                }
                catch (Exception ex)
                {
                    treeKillError = ex.Message;
                }
            }

            bool ownedJobTerminated = false;
            if (terminateOwnedJob != null)
            {
                try { ownedJobTerminated = terminateOwnedJob(); }
                catch (Exception ex) { treeKillError = AppendError(treeKillError, ex.Message); }
            }

            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex) { treeKillError = AppendError(treeKillError, ex.Message); }

            if (!await Task.Run(() => process.WaitForExit(10000)))
                throw new TimeoutException("セットアッププロセスを終了できませんでした。" +
                    (string.IsNullOrWhiteSpace(treeKillError) ? string.Empty : " " + treeKillError));

            if (isWindows && !string.IsNullOrWhiteSpace(treeKillError))
                throw new IOException("セットアップの子プロセス終了を確認できませんでした: " + treeKillError);
            if (!ownedJobTerminated && !process.HasExited)
                throw new IOException("セットアップのプロセス終了を確認できませんでした。");
        }

        private static string AppendError(string current, string next)
        {
            return string.IsNullOrWhiteSpace(current) ? next : current + Environment.NewLine + next;
        }
    }
}
