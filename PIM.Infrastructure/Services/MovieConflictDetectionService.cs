using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    public class MovieConflictDetectionService : IMovieConflictDetectionService
    {
        private readonly IDestinationConflictService _destinationConflictService;
        private readonly IPlexLibraryConflictService _plexLibraryConflictService;

        public MovieConflictDetectionService(
            IDestinationConflictService destinationConflictService,
            IPlexLibraryConflictService plexLibraryConflictService)
        {
            _destinationConflictService = destinationConflictService;
            _plexLibraryConflictService = plexLibraryConflictService;
        }

        public void ClearConflictState(List<Movie> movies)
        {
            foreach (var movie in movies)
            {
                movie.HasDestinationConflict = false;
                movie.DestinationConflictReason = null;
                movie.ExistingDestinationPath = null;

                movie.HasPlexLibraryConflict = false;
                movie.PlexLibraryConflictReason = null;
                movie.ExistingPlexLibraryPath = null;

                if (IsConflictReviewReason(movie.ReviewReason))
                {
                    movie.ReviewReason = null;

                    if (!movie.HasError)
                    {
                        movie.NeedsReview = false;
                        movie.ApprovedForCommit = ShouldBeApprovedForCommit(movie);
                    }
                }
            }
        }

        public void ApplyConflictDetection(List<Movie> movies, string outputPath)
        {
            ClearConflictState(movies);

            foreach (var movie in movies)
            {
                if (movie.HasError ||
                    movie.NeedsReview ||
                    string.IsNullOrWhiteSpace(movie.TargetPath) ||
                    !movie.ApprovedForCommit)
                {
                    continue;
                }

                var destinationResult = _destinationConflictService.Check(movie, outputPath);

                if (destinationResult.HasConflict)
                {
                    movie.HasDestinationConflict = true;
                    movie.DestinationConflictReason = destinationResult.Message;
                    movie.ExistingDestinationPath = destinationResult.ExistingPath;
                }

                var plexResult = _plexLibraryConflictService.Check(movie);

                if (plexResult.HasConflict)
                {
                    movie.HasPlexLibraryConflict = true;
                    movie.PlexLibraryConflictReason = plexResult.Message;
                    movie.ExistingPlexLibraryPath = plexResult.ExistingPath;
                }

                if (movie.HasDestinationConflict || movie.HasPlexLibraryConflict)
                {
                    MarkMovieForConflictReview(movie);
                }
            }
        }

        private static void MarkMovieForConflictReview(Movie movie)
        {
            var reasons = new List<string>();

            if (movie.HasDestinationConflict)
            {
                reasons.Add($"Destination conflict: {movie.DestinationConflictReason}");
            }

            if (movie.HasPlexLibraryConflict)
            {
                reasons.Add($"Plex library conflict: {movie.PlexLibraryConflictReason}");
            }

            movie.NeedsReview = true;
            movie.ApprovedForCommit = false;
            movie.Status = "Needs Review - Conflict Detected";
            movie.ReviewReason = string.Join(" | ", reasons);
        }

        private static bool ShouldBeApprovedForCommit(Movie movie)
        {
            return !movie.NeedsReview &&
                   !movie.HasError &&
                   (!movie.IsDuplicate || movie.KeepRecommended || movie.IsAlternateVersion);
        }

        private static bool IsConflictReviewReason(string? reviewReason)
        {
            if (string.IsNullOrWhiteSpace(reviewReason))
                return false;

            return reviewReason.Contains("Destination conflict:", StringComparison.OrdinalIgnoreCase) ||
                   reviewReason.Contains("Plex library conflict:", StringComparison.OrdinalIgnoreCase);
        }
    }
}