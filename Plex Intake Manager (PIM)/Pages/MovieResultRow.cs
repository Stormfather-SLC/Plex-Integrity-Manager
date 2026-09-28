using PIM.Core.Models;

namespace PIM.Web.Pages;

/// <summary>
/// What the owner needs to know about a scanned movie at a glance.
/// </summary>
public enum MovieResultState
{
    /// <summary>Needs review and the page offers a way to resolve it.</summary>
    Decide,

    /// <summary>Needs review, but the cause must be fixed outside PIM.</summary>
    Blocked,

    Error,

    /// <summary>Scanned but not identified yet.</summary>
    Pending,

    Ready,

    /// <summary>A duplicate copy that was not chosen; left in place, never deleted.</summary>
    Skipped
}

/// <summary>
/// Display model for one row of the Movie Results list: a state badge, a
/// one-line summary, the actions available, and short paths. Presentation only;
/// every decision still goes through the existing page handlers.
/// </summary>
public sealed class MovieResultRow
{
    private MovieResultRow(Movie movie)
    {
        Movie = movie;
    }

    public Movie Movie { get; }

    public MovieResultState State { get; private init; }

    public string Summary { get; private init; } = string.Empty;

    public IReadOnlyList<string> Reasons { get; private init; } = Array.Empty<string>();

    public string DisplayTitle { get; private init; } = string.Empty;

    public string SourceDisplay { get; private init; } = string.Empty;

    public string? TargetDisplay { get; private init; }

    public string FilterKey => State.ToString().ToLowerInvariant();

    public string StateLabel => State switch
    {
        MovieResultState.Decide => "Your decision",
        MovieResultState.Blocked => "Blocked",
        MovieResultState.Error => "Error",
        MovieResultState.Pending => "Not identified",
        MovieResultState.Ready => "Ready",
        _ => "Skipped"
    };

    public string BadgeClass => State switch
    {
        MovieResultState.Decide => "text-bg-primary",
        MovieResultState.Blocked => "text-bg-warning",
        MovieResultState.Error => "text-bg-danger",
        MovieResultState.Pending => "text-bg-light border",
        MovieResultState.Ready => "text-bg-success",
        _ => "text-bg-secondary"
    };

    /// <summary>Review reasons beyond the one the summary describes.</summary>
    public int AdditionalReasonCount =>
        State is MovieResultState.Decide or MovieResultState.Blocked
            ? Math.Max(0, Reasons.Count - 1)
            : 0;

    public bool CanAcceptSuggestion => Movie.NeedsReview && Movie.CanAcceptMetadataSuggestion;

    public bool CanKeepThisCopy => Movie.HasDuplicateTieReview;

    public bool CanConfirmFileName => Movie.HasSuspiciousFileNameReview;

    public bool CanAddPlexDuplicate => Movie.NeedsPlexDuplicateDecision;

    public bool CanUndoPlexDuplicate => Movie.IsPlexDuplicateAccepted;

    public bool CanEnterImdbId => Movie.NeedsReview && Movie.CanEnterImdbId;

    public static MovieResultRow Create(
        Movie movie,
        string? sourceRoot,
        string? destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(movie);

        var reasons = SplitReasons(movie.ReviewReason);
        var probe = new MovieResultRow(movie);
        var state = DetermineState(movie, probe);

        return new MovieResultRow(movie)
        {
            State = state,
            Reasons = reasons,
            Summary = BuildSummary(movie, state, reasons),
            DisplayTitle = string.IsNullOrWhiteSpace(movie.Title)
                ? movie.FileName ?? Path.GetFileName(movie.OriginalFilePath)
                : movie.Title,
            SourceDisplay = RelativeTo(sourceRoot, movie.OriginalFilePath) ?? string.Empty,
            TargetDisplay = RelativeTo(destinationRoot, movie.TargetPath)
        };
    }

    /// <summary>
    /// Priority for listing: things needing the owner first.
    /// </summary>
    public int SortOrder => State switch
    {
        MovieResultState.Decide => 0,
        MovieResultState.Blocked => 1,
        MovieResultState.Error => 2,
        MovieResultState.Pending => 3,
        MovieResultState.Ready => 4,
        _ => 5
    };

    private static MovieResultState DetermineState(Movie movie, MovieResultRow actions)
    {
        if (movie.HasError)
            return MovieResultState.Error;

        if (movie.NeedsReview)
        {
            var hasAction = actions.CanAcceptSuggestion ||
                            actions.CanKeepThisCopy ||
                            actions.CanConfirmFileName ||
                            actions.CanAddPlexDuplicate ||
                            actions.CanEnterImdbId;

            return hasAction ? MovieResultState.Decide : MovieResultState.Blocked;
        }

        if (movie.IsDuplicate && !movie.KeepRecommended && !movie.IsAlternateVersion)
            return MovieResultState.Skipped;

        if (movie.ApprovedForCommit)
            return MovieResultState.Ready;

        return MovieResultState.Pending;
    }

    private static string BuildSummary(
        Movie movie,
        MovieResultState state,
        IReadOnlyList<string> reasons)
    {
        switch (state)
        {
            case MovieResultState.Error:
                return movie.ErrorMessage ?? "Error";

            case MovieResultState.Decide:
            case MovieResultState.Blocked:
                if (movie.NeedsPlexDuplicateDecision)
                {
                    return "Possible duplicate: Plex already has this movie " +
                           $"({movie.ExistingPlexResolution ?? "resolution unknown"}, " +
                           $"{VideoResolution.FormatSize(movie.ExistingPlexSizeBytes)})";
                }

                if (movie.HasDuplicateTieReview)
                    return "Tied with another copy of this movie; choose which one to keep";

                if (movie.CanAcceptMetadataSuggestion && movie.HasMetadataReviewReason)
                {
                    return $"Possible match: {movie.SuggestedTitle} " +
                           $"({movie.SuggestedYear?.ToString() ?? "year unknown"})";
                }

                if (movie.HasSuspiciousFileNameReview && reasons.Count == 1)
                    return "Unusually long file name; check it and confirm";

                return reasons.FirstOrDefault() ?? "Needs review";

            case MovieResultState.Ready:
                if (movie.IsPlexDuplicateAccepted)
                    return "Will be added alongside the existing Plex copy (your decision)";

                if (movie.IsPlexTrackedMigration)
                    return "Plex tracks this file; it will be reorganized";

                if (movie.IsAlternateVersion)
                    return "Additional edition; will be moved";

                if (movie.KeepRecommended)
                    return "Best copy of this movie; will be moved";

                return "Ready to move";

            case MovieResultState.Skipped:
                return "Another copy is kept; this one stays where it is (not deleted)";

            default:
                return "Not identified yet; run Identify Movies";
        }
    }

    private static IReadOnlyList<string> SplitReasons(string? reviewReason)
    {
        return (reviewReason ?? string.Empty)
            .Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Shows a path relative to its root when it lies under that root, so rows
    /// stay short; otherwise returns the path unchanged.
    /// </summary>
    public static string? RelativeTo(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (string.IsNullOrWhiteSpace(root))
            return path;

        try
        {
            var fullRoot = Path.GetFullPath(root.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);

            return fullPath.StartsWith(
                       fullRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase)
                ? fullPath[(fullRoot.Length + 1)..]
                : path;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
