using PIM.Core.Models;

namespace PIM.Web.Pages;

/// <summary>
/// Display model for one line of the Dry Run Preview: what a live commit would
/// do with this file, in one line. Diagnostics live in Movie Results Details.
/// </summary>
public sealed class DryRunPreviewRow
{
    private DryRunPreviewRow(DryRunPreviewItem item)
    {
        Item = item;
    }

    public DryRunPreviewItem Item { get; }

    /// <summary>move, duplicate, review, or error (used by the filter cards).</summary>
    public string Category { get; private init; } = "other";

    public string BadgeLabel => Category switch
    {
        "move" => "Move",
        "duplicate" => "Skip",
        "review" => "Review",
        "error" => "Error",
        _ => Item.Action
    };

    public string BadgeClass => Category switch
    {
        "move" => "bg-success text-white",
        "duplicate" => "bg-secondary text-white",
        "review" => "bg-primary text-white",
        "error" => "bg-danger text-white",
        _ => "bg-light text-dark border"
    };

    public string DisplayTitle { get; private init; } = string.Empty;

    public string SourceDisplay { get; private init; } = string.Empty;

    public string? TargetDisplay { get; private init; }

    public string Note { get; private init; } = string.Empty;

    /// <summary>Review reasons beyond the one shown in <see cref="Note"/>.</summary>
    public int AdditionalReasonCount { get; private init; }

    /// <summary>
    /// Review and error items link to their Movie Results row, where they can
    /// be resolved. Null for older saved previews without a movie id.
    /// </summary>
    public string? MovieDetailsId =>
        Category is "review" or "error" && Item.MovieId != Guid.Empty
            ? $"details-{Item.MovieId:N}"
            : null;

    public static DryRunPreviewRow Create(
        DryRunPreviewItem item,
        string? sourceRoot,
        string? destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(item);

        var category = Categorize(item.Action);
        var reasons = (item.ReviewReason ?? string.Empty)
            .Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new DryRunPreviewRow(item)
        {
            Category = category,
            DisplayTitle = string.IsNullOrWhiteSpace(item.ParsedTitle)
                ? item.FileName
                : item.ParsedYear.HasValue
                    ? $"{item.ParsedTitle} ({item.ParsedYear})"
                    : item.ParsedTitle,
            SourceDisplay = MovieResultRow.RelativeTo(sourceRoot, item.OriginalFilePath) ?? string.Empty,
            TargetDisplay = category == "move"
                ? MovieResultRow.RelativeTo(destinationRoot, item.TargetPath)
                : null,
            Note = BuildNote(item, category, reasons),
            AdditionalReasonCount = category == "review"
                ? Math.Max(0, reasons.Length - 1)
                : 0
        };
    }

    private static string Categorize(string? action)
    {
        var value = action ?? string.Empty;

        if (value.Contains("Move", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Rename", StringComparison.OrdinalIgnoreCase))
        {
            return "move";
        }

        if (value.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Skip", StringComparison.OrdinalIgnoreCase))
        {
            return "duplicate";
        }

        if (value.Contains("Review", StringComparison.OrdinalIgnoreCase))
            return "review";

        return value.Contains("Error", StringComparison.OrdinalIgnoreCase)
            ? "error"
            : "other";
    }

    private static string BuildNote(
        DryRunPreviewItem item,
        string category,
        IReadOnlyList<string> reasons)
    {
        return category switch
        {
            "move" when item.IsPlexDuplicateAccepted =>
                "Added alongside the existing Plex copy (your decision)",
            "move" when item.IsPlexTrackedMigration =>
                "Plex tracks this file; it will be reorganized",
            "move" when item.IsAlternateVersion =>
                "Additional edition",
            "move" when item.KeepRecommended =>
                "Best copy of this movie",
            "move" => "Ready",
            "duplicate" => "Duplicate copy; left in place, not deleted",
            "review" => reasons.FirstOrDefault() ??
                        (string.IsNullOrWhiteSpace(item.Status) ? "Needs review" : item.Status),
            "error" => string.IsNullOrWhiteSpace(item.Status) ? "Error" : item.Status,
            _ => item.Status
        };
    }
}
