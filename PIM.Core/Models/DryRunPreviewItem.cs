namespace PIM.Core.Models;

public class DryRunPreviewItem
{
    public string FileName { get; set; } = string.Empty;

    public string OriginalFilePath { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string ReviewReason { get; set; } = string.Empty;

    public string ParsedTitle { get; set; } = string.Empty;

    public int? ParsedYear { get; set; }

    public string ParsedImdbId { get; set; } = string.Empty;

    public string Edition { get; set; } = string.Empty;

    public double? MetadataConfidence { get; set; }

    public string MetadataReviewReason { get; set; } = string.Empty;

    public string MetadataFailureDetail { get; set; } = string.Empty;

    public string SuggestedTitle { get; set; } = string.Empty;

    public int? SuggestedYear { get; set; }

    public string SuggestedImdbId { get; set; } = string.Empty;

    public MetadataMatchOrigin MetadataMatchOrigin { get; set; }

    public string MetadataDiscoveryReason { get; set; } = string.Empty;

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

    public bool IsPlexTrackedMigration { get; set; }

    public string PlexTrackedMigrationReason { get; set; } = string.Empty;

    public bool HasAnyConflict =>
        HasDestinationConflict || HasPlexLibraryConflict;

    public bool HasPathComparisonContext =>
        HasAnyConflict || IsPlexTrackedMigration;

    public bool HasMetadataSuggestion =>
        !string.IsNullOrWhiteSpace(SuggestedTitle) &&
        !string.IsNullOrWhiteSpace(SuggestedImdbId);
}
