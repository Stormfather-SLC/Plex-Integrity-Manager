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
        private int _warningCount;

        public void Reset(bool enabled)
        {
            lock (_sync)
            {
                _enabled = enabled;
                _attempted = false;
                _emptyFoldersRemoved = 0;
                _warningCount = 0;
            }
        }

        public void Complete(int emptyFoldersRemoved, int warningCount)
        {
            lock (_sync)
            {
                _attempted = true;
                _emptyFoldersRemoved = Math.Max(0, emptyFoldersRemoved);
                _warningCount = Math.Max(0, warningCount);
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
                    _warningCount);
            }
        }
    }

    public sealed record SourceCleanupSnapshot(
        bool Enabled,
        bool Attempted,
        int EmptyFoldersRemoved,
        int WarningCount);
}
