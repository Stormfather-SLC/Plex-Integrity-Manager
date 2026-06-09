namespace PIM.Core.Models;

public class DryRunPreviewItem
{
    public string FileName { get; set; } = string.Empty;

    public string OriginalFilePath { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public bool IsDuplicate { get; set; }

    public bool KeepRecommended { get; set; }

    public bool NeedsReview { get; set; }

    public string CssClass { get; set; } = string.Empty;
}