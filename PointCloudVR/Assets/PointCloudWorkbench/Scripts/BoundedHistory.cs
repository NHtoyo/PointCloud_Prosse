using System;
using System.Collections.Generic;

namespace PointCloudWorkbench
{
    public sealed class BoundedHistory<T>
    {
        private readonly List<T> items = new List<T>();
        private readonly int capacity;
        private readonly long maxBytes;
        private readonly Func<T, long> sizeOf;
        private long retainedBytes;

        public int Count => items.Count;
        public long RetainedBytes => retainedBytes;

        public BoundedHistory(int capacity)
            : this(capacity, long.MaxValue, null)
        {
        }

        public BoundedHistory(int capacity, long maxBytes, Func<T, long> sizeOf)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (sizeOf == null && maxBytes != long.MaxValue)
                throw new ArgumentNullException(nameof(sizeOf));
            this.capacity = capacity;
            this.maxBytes = maxBytes;
            this.sizeOf = sizeOf;
        }

        public bool Push(T item)
        {
            long itemBytes = sizeOf == null ? 0 : sizeOf(item);
            if (itemBytes < 0) throw new ArgumentOutOfRangeException(nameof(item));
            if (itemBytes > maxBytes)
            {
                Clear();
                return false;
            }
            while (items.Count > 0 &&
                   (items.Count == capacity || retainedBytes > maxBytes - itemBytes))
            {
                RemoveOldest();
            }
            items.Add(item);
            retainedBytes += itemBytes;
            return true;
        }

        public T Pop()
        {
            if (items.Count == 0) throw new InvalidOperationException("History is empty.");
            int last = items.Count - 1;
            T item = items[last];
            items.RemoveAt(last);
            if (sizeOf != null) retainedBytes -= sizeOf(item);
            return item;
        }

        public T Peek()
        {
            if (items.Count == 0) throw new InvalidOperationException("History is empty.");
            return items[items.Count - 1];
        }

        public void Clear()
        {
            items.Clear();
            retainedBytes = 0;
        }

        private void RemoveOldest()
        {
            T item = items[0];
            items.RemoveAt(0);
            if (sizeOf != null) retainedBytes -= sizeOf(item);
        }
    }
}
