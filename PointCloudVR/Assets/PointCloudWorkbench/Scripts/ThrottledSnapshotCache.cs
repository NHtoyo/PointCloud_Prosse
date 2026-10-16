using System;
using System.Diagnostics;
using System.Threading;

namespace PointCloudWorkbench
{
    public sealed class ThrottledSnapshotCache<T> where T : class
    {
        private readonly long refreshIntervalTicks;
        private T value;
        private long storedAtTicks;

        public ThrottledSnapshotCache(TimeSpan refreshInterval)
        {
            if (refreshInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(refreshInterval));
            refreshIntervalTicks = Math.Max(1L, (long)(refreshInterval.TotalSeconds * Stopwatch.Frequency));
        }

        public bool TryGet(out T cachedValue)
        {
            cachedValue = Interlocked.CompareExchange(ref value, null, null);
            if (cachedValue == null) return false;
            long ageTicks = Stopwatch.GetTimestamp() - Interlocked.Read(ref storedAtTicks);
            return ageTicks >= 0 && ageTicks < refreshIntervalTicks;
        }

        public void Store(T newValue)
        {
            if (newValue == null) throw new ArgumentNullException(nameof(newValue));
            Interlocked.Exchange(ref value, newValue);
            Interlocked.Exchange(ref storedAtTicks, Stopwatch.GetTimestamp());
        }
    }
}
