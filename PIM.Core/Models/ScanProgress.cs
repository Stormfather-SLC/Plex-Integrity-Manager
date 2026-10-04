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

        private int _busy;
        private string? _busyWith;

        /// <summary>
        /// True while a scan, identify, dry run, live commit, review decision,
        /// or settings change is in progress. PIM runs one of these at a time
        /// because they all read and rewrite the same scanned movie list.
        /// </summary>
        public bool IsBusy => Volatile.Read(ref _busy) == 1;

        /// <summary>
        /// The action that currently holds PIM's one-at-a-time gate, in words
        /// suitable for the page, or null when PIM is idle.
        /// </summary>
        public string? BusyWith => IsBusy ? Volatile.Read(ref _busyWith) : null;

        /// <summary>
        /// Claims the one-at-a-time gate. Returns false, changing nothing,
        /// when another action already holds it. A caller that receives true
        /// must call <see cref="EndAction"/> when its work ends.
        /// </summary>
        public bool TryBeginAction(string action)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return false;

            Volatile.Write(ref _busyWith, action);
            return true;
        }

        /// <summary>
        /// Releases the gate. Only the caller whose
        /// <see cref="TryBeginAction"/> returned true may call this.
        /// </summary>
        public void EndAction()
        {
            Volatile.Write(ref _busyWith, null);
            Volatile.Write(ref _busy, 0);
        }

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
