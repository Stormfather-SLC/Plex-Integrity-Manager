namespace PIM.Core.Models
{
    /// <summary>
    /// Stores the source-folder cleanup result for the most recent file operation.
    /// PIM is currently a single-user local application, so one shared status object
    /// is sufficient for the completion summary shown in the browser.
    /// </summary>
    public sealed class SourceCleanupStatus
    {
        private readonly object _sync = new();
        private bool _enabled;
        private bool _attempted;
        private int _emptyFoldersRemoved;
        private int _protectedFoldersPreserved;
        private int _nonEmptyFoldersPreserved;
        private int _failedFolderCount;

        public void Reset(bool enabled)
        {
            lock (_sync)
            {
                _enabled = enabled;
                _attempted = false;
                _emptyFoldersRemoved = 0;
                _protectedFoldersPreserved = 0;
                _nonEmptyFoldersPreserved = 0;
                _failedFolderCount = 0;
            }
        }

        public void Complete(
            int emptyFoldersRemoved,
            int protectedFoldersPreserved,
            int nonEmptyFoldersPreserved,
            int failedFolderCount)
        {
            lock (_sync)
            {
                _attempted = true;
                _emptyFoldersRemoved = Math.Max(0, emptyFoldersRemoved);
                _protectedFoldersPreserved = Math.Max(0, protectedFoldersPreserved);
                _nonEmptyFoldersPreserved = Math.Max(0, nonEmptyFoldersPreserved);
                _failedFolderCount = Math.Max(0, failedFolderCount);
            }
        }

        public SourceCleanupSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new SourceCleanupSnapshot(
                    _enabled,
                    _attempted,
                    _emptyFoldersRemoved,
                    _protectedFoldersPreserved,
                    _nonEmptyFoldersPreserved,
                    _failedFolderCount);
            }
        }
    }

    public sealed record SourceCleanupSnapshot(
        bool Enabled,
        bool Attempted,
        int EmptyFoldersRemoved,
        int ProtectedFoldersPreserved,
        int NonEmptyFoldersPreserved,
        int FailedFolderCount)
    {
        /// <summary>
        /// Backward-compatible summary value used by older browser code.
        /// Cleanup failures are the only conditions that should raise a warning.
        /// </summary>
        public int WarningCount => FailedFolderCount;
    }
}
