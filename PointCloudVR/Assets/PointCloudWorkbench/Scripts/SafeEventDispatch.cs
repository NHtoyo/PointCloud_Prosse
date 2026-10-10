using System;

namespace PointCloudWorkbench
{
    public static class SafeEventDispatch
    {
        public static void InvokeEach<T>(Action<T> handlers, T value, Action<Exception> onError)
        {
            if (handlers == null) return;
            Delegate[] callbacks = handlers.GetInvocationList();
            for (int i = 0; i < callbacks.Length; i++)
            {
                try
                {
                    ((Action<T>)callbacks[i])(value);
                }
                catch (Exception exception)
                {
                    onError?.Invoke(exception);
                }
            }
        }
    }
}
