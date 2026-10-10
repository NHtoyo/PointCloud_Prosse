using System;
using System.Threading;

namespace PointCloudWorkbench
{
    public sealed class PointCloudOperation
    {
        private readonly PointCloudProgressManager owner;
        private readonly long operationId;

        public CancellationToken CancellationToken { get; }
        public bool IsCurrent => owner.IsCurrent(operationId);
        public bool IsCancellationRequested => CancellationToken.IsCancellationRequested;

        internal PointCloudOperation(PointCloudProgressManager owner, long operationId, CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.operationId = operationId;
            CancellationToken = cancellationToken;
        }

        public bool Update(float value, string message = null) => owner.Update(operationId, value, message);
        public bool Complete() => owner.Finish(operationId, PointCloudOperationStatus.Success, false, false, string.Empty, string.Empty);
        public bool CompleteCancelled(string message = "処理をキャンセルしました。") =>
            owner.Finish(operationId, PointCloudOperationStatus.Cancelled, false, false, message, string.Empty);
        public bool CompleteWithWarning(string message, string exceptionDetail) =>
            owner.Finish(operationId, PointCloudOperationStatus.SuccessWithWarning, false, true, message, exceptionDetail);
        public bool Fail(string operationTitle, string message, string exceptionDetail = null) =>
            owner.Finish(operationId, PointCloudOperationStatus.Failed, true, false, message,
                exceptionDetail ?? message, operationTitle);
        public bool Cancel() => owner.Cancel(operationId);
    }

    public enum PointCloudOperationStatus
    {
        Idle,
        Running,
        Success,
        Cancelled,
        Failed,
        SuccessWithWarning
    }

    public sealed class PointCloudProgressSnapshot
    {
        public bool IsRunning { get; }
        public bool HasError { get; }
        public bool HasWarning { get; }
        public float Progress { get; }
        public string Title { get; }
        public string StatusMessage { get; }
        public string NotificationMessage { get; }
        public string Detail { get; }
        public PointCloudOperationStatus OperationStatus { get; }

        internal PointCloudProgressSnapshot(bool isRunning, bool hasError, bool hasWarning, float progress,
            string title, string statusMessage, string notificationMessage, string detail,
            PointCloudOperationStatus operationStatus)
        {
            IsRunning = isRunning;
            HasError = hasError;
            HasWarning = hasWarning;
            Progress = progress;
            Title = title;
            StatusMessage = statusMessage;
            NotificationMessage = notificationMessage;
            Detail = detail;
            OperationStatus = operationStatus;
        }
    }

    public sealed class PointCloudProgressManager
    {
        private static readonly PointCloudProgressManager instance = new PointCloudProgressManager();
        public static PointCloudProgressManager Instance => instance;

        private readonly object sync = new object();
        private bool isRunning;
        private bool hasError;
        private bool hasWarning;
        private float progress;
        private string title = string.Empty;
        private string statusMessage = string.Empty;
        private string notificationMessage = string.Empty;
        private string detail = string.Empty;
        private PointCloudOperationStatus operationStatus = PointCloudOperationStatus.Idle;
        private CancellationTokenSource cts;
        private long nextOperationId;
        private long currentOperationId;

        public bool IsRunning { get { lock (sync) return isRunning; } }
        public bool IsError { get { lock (sync) return hasError; } }
        public bool HasError { get { lock (sync) return hasError; } }
        public bool HasWarning { get { lock (sync) return hasWarning; } }
        public float Progress { get { lock (sync) return progress; } }
        public string Title { get { lock (sync) return title; } }
        public string StatusMessage { get { lock (sync) return statusMessage; } }
        public string ErrorMessage { get { lock (sync) return notificationMessage; } }
        public PointCloudOperationStatus OperationStatus { get { lock (sync) return operationStatus; } }
        public CancellationToken CancellationToken { get { lock (sync) return cts != null ? cts.Token : CancellationToken.None; } }

        public PointCloudProgressSnapshot GetSnapshot()
        {
            lock (sync)
            {
                return new PointCloudProgressSnapshot(isRunning, hasError, hasWarning, progress,
                    title, statusMessage, notificationMessage, detail, operationStatus);
            }
        }

        public PointCloudOperation TryStart(string operationTitle, string message)
        {
            CancellationTokenSource oldSource;
            PointCloudOperation operation;
            lock (sync)
            {
                if (isRunning) return null;
                oldSource = cts;
                cts = new CancellationTokenSource();
                currentOperationId = ++nextOperationId;
                isRunning = true;
                hasError = false;
                hasWarning = false;
                progress = 0f;
                title = operationTitle ?? string.Empty;
                statusMessage = message ?? string.Empty;
                notificationMessage = string.Empty;
                detail = string.Empty;
                operationStatus = PointCloudOperationStatus.Running;
                operation = new PointCloudOperation(this, currentOperationId, cts.Token);
            }
            oldSource?.Dispose();
            return operation;
        }

        internal bool IsCurrent(long operationId)
        {
            lock (sync)
            {
                return isRunning && currentOperationId == operationId;
            }
        }

        internal bool Update(long operationId, float value, string message)
        {
            lock (sync)
            {
                if (!isRunning || currentOperationId != operationId) return false;
                progress = Math.Max(0f, Math.Min(1f, value));
                if (message != null) statusMessage = message;
                return true;
            }
        }

        public void Cancel()
        {
            long operationId;
            lock (sync) operationId = currentOperationId;
            Cancel(operationId);
        }

        internal bool Cancel(long operationId)
        {
            CancellationTokenSource source;
            lock (sync)
            {
                if (!isRunning || currentOperationId != operationId) return false;
                source = cts;
                statusMessage = "ユーザーによるキャンセルをリクエスト中...";
            }
            try { source?.Cancel(); }
            catch (ObjectDisposedException) { }
            return true;
        }

        public void ShowError(string operationTitle, string message)
        {
            lock (sync)
            {
                if (isRunning) return;
                hasError = true;
                hasWarning = false;
                title = operationTitle ?? string.Empty;
                notificationMessage = message ?? string.Empty;
                detail = notificationMessage;
                operationStatus = PointCloudOperationStatus.Failed;
            }
        }

        public void DismissNotification()
        {
            lock (sync)
            {
                if (isRunning) return;
                hasError = false;
                hasWarning = false;
                notificationMessage = string.Empty;
                detail = string.Empty;
            }
        }

        internal bool Finish(long operationId, PointCloudOperationStatus status, bool error, bool warning, string message,
            string exceptionDetail, string operationTitle = null)
        {
            CancellationTokenSource source;
            lock (sync)
            {
                if (!isRunning || currentOperationId != operationId) return false;
                isRunning = false;
                hasError = error;
                hasWarning = warning;
                if (operationTitle != null) title = operationTitle;
                notificationMessage = message ?? string.Empty;
                detail = exceptionDetail ?? string.Empty;
                operationStatus = status;
                if (status == PointCloudOperationStatus.Success) progress = 1f;
                if (status == PointCloudOperationStatus.Failed) statusMessage = "処理に失敗しました。";
                if (status == PointCloudOperationStatus.Cancelled) statusMessage = message ?? "処理をキャンセルしました。";
                source = cts;
                cts = null;
            }
            source?.Dispose();
            return true;
        }
    }
}
