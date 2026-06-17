namespace PIM.Core.Models;

public class DryRunPreviewItem
{
    public string FileName { get; set; } = string.Empty;

    public string OriginalFilePath { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string CssClass { get; set; } = string.Empty;

    public bool IsDuplicate { get; set; }

    public bool KeepRecommended { get; set; }

    public bool NeedsReview { get; set; }

    public bool HasError { get; set; }

    public bool IsAlternateVersion { get; set; }

    // =========================================================
    // 🛑 Conflict Display
    // =========================================================

    public bool HasDestinationConflict { get; set; }

    public string DestinationConflictReason { get; set; } = string.Empty;

    public string ExistingDestinationPath { get; set; } = string.Empty;

    public bool HasPlexLibraryConflict { get; set; }

    public string PlexLibraryConflictReason { get; set; } = string.Empty;

    public string ExistingPlexLibraryPath { get; set; } = string.Empty;

    public bool HasAnyConflict =>
        HasDestinationConflict || HasPlexLibraryConflict;
}