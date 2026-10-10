using System;
using System.Text;

namespace PointCloudWorkbench
{
    public sealed class BoundedTextBuffer
    {
        public const int DefaultCapacity = 256 * 1024;
        private readonly object sync = new object();
        private readonly char[] head;
        private readonly char[] tail;
        private int headCount;
        private int tailCount;
        private int tailWriteIndex;
        private long totalCharacters;

        public int Length
        {
            get
            {
                lock (sync) return headCount + tailCount;
            }
        }

        public long TruncatedCharacters
        {
            get
            {
                lock (sync) return Math.Max(0L, totalCharacters - headCount - tailCount);
            }
        }

        public BoundedTextBuffer(int capacity = DefaultCapacity, int headCapacity = 16 * 1024)
        {
            if (capacity < 2 || headCapacity < 1 || headCapacity >= capacity)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            head = new char[headCapacity];
            tail = new char[capacity - headCapacity];
        }

        public void Append(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (sync)
            {
                totalCharacters += value.Length;
                for (int i = 0; i < value.Length; i++)
                {
                    char character = value[i];
                    if (headCount < head.Length)
                    {
                        head[headCount++] = character;
                        continue;
                    }

                    tail[tailWriteIndex] = character;
                    tailWriteIndex = (tailWriteIndex + 1) % tail.Length;
                    if (tailCount < tail.Length) tailCount++;
                }
            }
        }

        public void AppendLine(string value)
        {
            Append(value ?? string.Empty);
            Append("\n");
        }

        public void Clear()
        {
            lock (sync)
            {
                headCount = 0;
                tailCount = 0;
                tailWriteIndex = 0;
                totalCharacters = 0;
            }
        }

        public override string ToString()
        {
            lock (sync)
            {
                StringBuilder builder = new StringBuilder(headCount + tailCount + 64);
                builder.Append(head, 0, headCount);
                long omitted = totalCharacters - headCount - tailCount;
                if (omitted > 0) builder.Append("\n...[truncated ").Append(omitted).Append(" characters]...\n");
                int first = tailCount == tail.Length ? tailWriteIndex : 0;
                for (int i = 0; i < tailCount; i++) builder.Append(tail[(first + i) % tail.Length]);
                return builder.ToString();
            }
        }
    }
}
