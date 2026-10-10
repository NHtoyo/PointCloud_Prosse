using System;

namespace PointCloudWorkbench
{
    public static class AtomicResourceFactory
    {
        public static TResource CreateInitialized<TData, TResource>(
            TData data,
            Func<TResource> create,
            Action<TResource, TData> initialize,
            Action<TResource> release)
            where TResource : class
        {
            TResource candidate = create();
            if (candidate == null) throw new InvalidOperationException("Resource creation returned null.");

            try
            {
                initialize(candidate, data);
                return candidate;
            }
            catch
            {
                try { release(candidate); }
                catch { }
                throw;
            }
        }
    }
}
