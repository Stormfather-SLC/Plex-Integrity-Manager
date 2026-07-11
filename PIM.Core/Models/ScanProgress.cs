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

        public int Processed { get; set; }

        public string CurrentFile { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public bool IsRunning { get; set; }
    }
}
