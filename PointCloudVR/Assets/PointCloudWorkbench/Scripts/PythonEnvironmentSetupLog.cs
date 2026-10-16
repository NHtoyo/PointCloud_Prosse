using System;
using System.IO;
using System.Text;

namespace PointCloudWorkbench
{
    public sealed class PythonEnvironmentSetupLog
    {
        public const int DefaultTailCapacity = 8 * 1024;

        private const long MaximumEventLogBytes = 16L * 1024L * 1024L;
        private readonly object appendLock = new object();
        private readonly object stateLock = new object();
        private readonly BoundedTextBuffer tail;
        private StreamWriter writer;
        private bool limitNotified;
        private long revision;
        private long cachedRevision = -1;
        private string cachedTail = string.Empty;

        public PythonEnvironmentSetupLog(int tailCapacity = DefaultTailCapacity)
        {
            int headCapacity = Math.Max(1, Math.Min(1024, tailCapacity / 8));
            tail = new BoundedTextBuffer(tailCapacity, headCapacity);
        }

        public void Clear()
        {
            lock (stateLock)
            {
                tail.Clear();
                revision++;
                cachedRevision = -1;
            }
        }

        public void SetWriter(StreamWriter value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            lock (appendLock)
            {
                if (writer != null) throw new InvalidOperationException("セットアップログは既に開かれています。");
                writer = value;
                limitNotified = false;
            }
        }

        public string AppendLine(string line, bool important)
        {
            line = line ?? string.Empty;
            string tailLine = LimitTailLine(line);
            lock (appendLock)
            {
                lock (stateLock)
                {
                    tail.AppendLine(tailLine);
                    revision++;
                    cachedRevision = -1;
                }

                if (writer == null) return null;
                try
                {
                    if (writer.BaseStream.Length < MaximumEventLogBytes || important)
                    {
                        writer.WriteLine(line);
                    }
                    else if (!limitNotified)
                    {
                        limitNotified = true;
                        writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff",
                            System.Globalization.CultureInfo.InvariantCulture) +
                            " [WARN] セットアップイベントログが16 MiBに達しました。以降の大量出力はpip詳細ログを参照してください。重要な失敗・警告は引き続き記録します。");
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    return "セットアップログを書き込めません: " + ex.Message;
                }
            }
        }

        public void GetTail(out string value, out long currentRevision)
        {
            lock (stateLock)
            {
                if (cachedRevision != revision)
                {
                    cachedTail = tail.ToString();
                    cachedRevision = revision;
                }
                value = cachedTail;
                currentRevision = revision;
            }
        }

        public void DisposeWriter()
        {
            lock (appendLock)
            {
                StreamWriter closing = writer;
                writer = null;
                if (closing != null) closing.Dispose();
            }
        }

        private string LimitTailLine(string line)
        {
            if (line.Length <= DefaultTailCapacity) return line;
            int half = (DefaultTailCapacity - 48) / 2;
            return line.Substring(0, half) + " ...[GUI log line truncated]... " +
                line.Substring(line.Length - half);
        }
    }
}
