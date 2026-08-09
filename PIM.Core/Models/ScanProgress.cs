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
    }
}
