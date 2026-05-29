using System;
using System.Threading;

namespace PointCloudWorkbench
{
    public class PointCloudProgressManager
    {
        private static PointCloudProgressManager instance;
        public static PointCloudProgressManager Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new PointCloudProgressManager();
                }
                return instance;
            }
        }

        public bool IsRunning { get; private set; }
        public float Progress { get; private set; } // 0.0f to 1.0f
        public string Title { get; private set; }
        public string StatusMessage { get; private set; }

        // エラーハンドリング用
        public bool IsError { get; private set; }
        public string ErrorMessage { get; private set; }

        private CancellationTokenSource cts;
        public CancellationToken CancellationToken => cts != null ? cts.Token : CancellationToken.None;

        public void Start(string title, string message)
        {
            IsRunning = true;
            IsError = false;
            ErrorMessage = "";
            Progress = 0f;
            Title = title;
            StatusMessage = message;
            if (cts != null)
            {
                cts.Dispose();
            }
            cts = new CancellationTokenSource();
        }

        public void Update(float progress, string message = null)
        {
            Progress = Math.Clamp(progress, 0f, 1f);
            if (message != null)
            {
                StatusMessage = message;
            }
        }

        public void Cancel()
        {
            if (cts != null)
            {
                cts.Cancel();
                StatusMessage = "ユーザーによるキャンセルをリクエスト中...";
            }
        }

        public void Complete()
        {
            IsRunning = false;
            IsError = false;
            ErrorMessage = "";
            Progress = 1f;
            if (cts != null)
            {
                cts.Dispose();
                cts = null;
            }
        }

        public void ShowError(string title, string errorMessage)
        {
            IsRunning = true;
            IsError = true;
            Title = title;
            ErrorMessage = errorMessage;
            StatusMessage = "エラーが発生しました。";
            Progress = 0f;
        }
    }
}
