using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services;

public class DryRunPreviewService : IDryRunPreviewService
{
    public DryRunPreviewResult BuildPreview(IEnumerable<Movie> movies)
    {
        var result = new DryRunPreviewResult();

        foreach (var movie in movies)
        {
            var item = BuildItem(movie);

            result.Items.Add(item);
            result.TotalFiles++;

            switch (item.Action)
            {
                case "Move / Rename":
                    result.ReadyToMoveCount++;
                    break;

                case "Duplicate - Skip":
                    result.DuplicateSkipCount++;
                    break;

                case "Needs Review":
                    result.NeedsReviewCount++;
                    break;

                case "Error":
                    result.ErrorCount++;
                    break;
            }
        }

        return result;
    }

    private DryRunPreviewItem BuildItem(Movie movie)
    {
        var item = new DryRunPreviewItem
        {
            FileName = movie.FileName ?? string.Empty,
            OriginalFilePath = movie.OriginalFilePath ?? string.Empty,
            TargetPath = movie.TargetPath ?? string.Empty,
            Status = movie.Status ?? string.Empty,
            IsDuplicate = movie.IsDuplicate,
            KeepRecommended = movie.KeepRecommended,
            NeedsReview = movie.NeedsReview,
            HasError = movie.HasError,
            IsAlternateVersion = movie.IsAlternateVersion,

            HasDestinationConflict = movie.HasDestinationConflict,
            DestinationConflictReason = movie.DestinationConflictReason ?? string.Empty,
            ExistingDestinationPath = movie.ExistingDestinationPath ?? string.Empty,

            HasPlexLibraryConflict = movie.HasPlexLibraryConflict,
            PlexLibraryConflictReason = movie.PlexLibraryConflictReason ?? string.Empty,
            ExistingPlexLibraryPath = movie.ExistingPlexLibraryPath ?? string.Empty
        };

        if (movie.NeedsReview)
        {
            item.Action = "Needs Review";
            item.Status = movie.ReviewReason ?? "Review required before commit";

            // For normal review rows, TargetPath may not be useful or safe.
            // For conflict rows, keep the proposed target visible so the user can
            // compare it against the existing destination/Plex path.
            if (!item.HasAnyConflict)
            {
                item.TargetPath = string.Empty;
            }

            item.CssClass = "dryrun-review";
        }
        else if (movie.HasError)
        {
            item.Action = "Error";
            item.Status = movie.ErrorMessage ?? movie.Status ?? "Error requires attention before commit";
            item.TargetPath = string.Empty;
            item.CssClass = "dryrun-error";
        }
        else if (movie.IsDuplicate && !movie.KeepRecommended && !movie.IsAlternateVersion)
        {
            item.Action = "Duplicate - Skip";
            item.Status = "Duplicate skipped";
            item.TargetPath = string.Empty;
            item.CssClass = "dryrun-duplicate";
        }
        else
        {
            item.Action = "Move / Rename";
            item.Status = movie.Status ?? "Ready to move";
            item.CssClass = "dryrun-move";
        }

        return item;
    }
}