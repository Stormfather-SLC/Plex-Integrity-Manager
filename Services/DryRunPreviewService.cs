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
            item.CssClass = "dryrun-review";
        }
        else if (string.IsNullOrWhiteSpace(movie.TargetPath))
        {
            item.Action = "Error";
            item.CssClass = "dryrun-error";
        }
        else if (movie.IsDuplicate && !movie.KeepRecommended)
        {
            item.Action = "Skip Duplicate";
            item.CssClass = "dryrun-duplicate";
        }
        else
        {
            item.Action = "Move/Rename";
            item.CssClass = "dryrun-ready";
        }

        return item;
    }
}