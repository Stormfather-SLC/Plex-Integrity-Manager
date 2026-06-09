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
                case "Move/Rename":
                    result.ReadyToMoveCount++;
                    break;

                case "Skip Duplicate":
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
            FileName = movie.FileName,
            OriginalFilePath = movie.OriginalFilePath,
            TargetPath = movie.TargetPath ?? string.Empty,
            Status = movie.Status ?? string.Empty,
            IsDuplicate = movie.IsDuplicate,
            KeepRecommended = movie.KeepRecommended,
            NeedsReview = movie.NeedsReview
        };

        if (movie.NeedsReview)
        {
            item.Action = "Needs Review";
            item.Status = movie.ReviewReason ?? "Review required before commit";
            item.TargetPath = string.Empty;
            item.CssClass = "dryrun-review";
        }
        else if (movie.HasError)
        {
            item.Action = "Error";
            item.Status = movie.ErrorMessage ?? movie.Status ?? "Error requires attention before commit";
            item.TargetPath = string.Empty;
            item.CssClass = "dryrun-error";
        }
        else if (string.IsNullOrWhiteSpace(movie.TargetPath))
        {
            item.Action = "Error";
            item.Status = "Missing target path";
            item.CssClass = "dryrun-error";
        }
        else if (movie.IsDuplicate && !movie.KeepRecommended && !movie.IsAlternateVersion)
        {
            item.Action = "Skip Duplicate";
            item.Status = "Duplicate would be skipped";
            item.CssClass = "dryrun-duplicate";
        }
        else
        {
            item.Action = "Move/Rename";
            item.Status = movie.Status ?? "Rename preview generated";
            item.CssClass = "dryrun-ready";
        }

        return item;
    }
}