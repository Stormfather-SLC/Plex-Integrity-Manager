namespace PIM.Core.Models;

public class DryRunPreviewResult
{
    public LibraryGoal LibraryGoal { get; set; } = LibraryGoal.Consolidation;

    public int TotalFiles { get; set; }
    public int ReadyToMoveCount { get; set; }
    public int DuplicateSkipCount { get; set; }
    public int NeedsReviewCount { get; set; }
    public int ErrorCount { get; set; }

    public List<DryRunPreviewItem> Items { get; set; } = new();
}
