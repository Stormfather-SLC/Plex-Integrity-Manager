namespace PIM.Core.Models
{
    /// <summary>
    /// Shared progress state for scan, metadata, conflict-check, dry-run, and
    /// live-commit operations. The Razor page polls this object while work is
    /// running so users can see that PIM is active and what it is doing.
    /// </summary>
    public class ScanProgress
    {
        public int Total { get; set; }

        /// <summary>
        /// Number of items whose processing has fully completed.
        /// </summary>
        public int Processed { get; set; }

        /// <summary>
        /// One-based position of the item currently being processed. A value of
        /// zero means there is no active item, such as during destination scanning.
        /// </summary>
        public int CurrentItem { get; set; }

        public string CurrentFile { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public bool IsRunning { get; set; }

        private readonly object _cancellationLock = new();
        private CancellationTokenSource? _cancellation;

        /// <summary>
        /// True while an operation that honours Cancel is running and no
        /// cancellation has been requested yet.
        /// </summary>
        public bool CanCancel
        {
            get
            {
                lock (_cancellationLock)
                {
                    return _cancellation != null &&
                           !_cancellation.IsCancellationRequested;
                }
            }
        }

        public bool CancelRequested
        {
            get
            {
                lock (_cancellationLock)
                {
                    return _cancellation?.IsCancellationRequested == true;
                }
            }
        }

        /// <summary>
        /// Starts a cancellable operation and returns its token. Any earlier
        /// operation's token is cancelled and released.
        /// </summary>
        public CancellationToken BeginCancellableOperation()
        {
            lock (_cancellationLock)
            {
                _cancellation?.Cancel();
                _cancellation?.Dispose();
                _cancellation = new CancellationTokenSource();
                return _cancellation.Token;
            }
        }

        /// <summary>
        /// Asks the running operation to stop at its next safe point. Returns
        /// false when nothing cancellable is running.
        /// </summary>
        public bool RequestCancel()
        {
            lock (_cancellationLock)
            {
                if (_cancellation == null || _cancellation.IsCancellationRequested)
                    return false;

                _cancellation.Cancel();
                return true;
            }
        }

        public void EndCancellableOperation()
        {
            lock (_cancellationLock)
            {
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }
}
