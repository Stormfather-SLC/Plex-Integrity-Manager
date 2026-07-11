using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    public class MovieConflictDetectionService : IMovieConflictDetectionService
    {
        private const string StandardEditionKey = "<standard>";

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
                        movie.Status = "Rename preview generated";
                    }
                }
            }
        }

        public void ApplyConflictDetection(List<Movie> movies, string outputPath)
        {
            // Remember prior conflict rows before clearing their transient state.
            // This allows a commit-time recheck to evaluate the same files again.
            var previousConflictIds = movies
                .Where(movie => IsConflictReviewReason(movie.ReviewReason))
                .Select(movie => movie.Id)
                .ToHashSet();

            ClearConflictState(movies);
            _destinationConflictService.InvalidateCache();

            var candidates = movies
                .Where(movie => IsConflictCandidate(movie, previousConflictIds))
                .ToList();

            ApplyIncomingTargetPathCollisions(candidates);
            ApplyIncomingSameEditionCollisions(candidates);

            foreach (var movie in candidates)
            {
                if (movie.HasDestinationConflict || movie.NeedsReview)
                    continue;

                var destinationResult = _destinationConflictService.Check(
                    movie,
                    outputPath);

                if (destinationResult.HasConflict)
                {
                    SetDestinationConflict(
                        movie,
                        destinationResult.Message,
                        destinationResult.ExistingPath);
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

        private static void ApplyIncomingTargetPathCollisions(
            List<Movie> candidates)
        {
            var collisionGroups = candidates
                .GroupBy(
                    movie => NormalizePath(movie.TargetPath!),
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            foreach (var group in collisionGroups)
            {
                var groupMovies = group.ToList();

                foreach (var movie in groupMovies)
                {
                    var otherMovie = groupMovies
                        .First(other => other.Id != movie.Id);

                    SetDestinationConflict(
                        movie,
                        "Multiple incoming files resolve to the same target path.",
                        otherMovie.OriginalFilePath);

                    MarkMovieForConflictReview(movie);
                }
            }
        }

        private static void ApplyIncomingSameEditionCollisions(
            List<Movie> candidates)
        {
            var collisionGroups = candidates
                .Where(movie =>
                    !movie.HasDestinationConflict &&
                    !string.IsNullOrWhiteSpace(movie.ImdbId))
                .GroupBy(
                    BuildMovieEditionIdentityKey,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            foreach (var group in collisionGroups)
            {
                var groupMovies = group.ToList();

                foreach (var movie in groupMovies)
                {
                    var otherMovie = groupMovies
                        .First(other => other.Id != movie.Id);

                    SetDestinationConflict(
                        movie,
                        "Multiple incoming files represent the same IMDb ID and edition.",
                        otherMovie.OriginalFilePath);

                    MarkMovieForConflictReview(movie);
                }
            }
        }

        private static bool IsConflictCandidate(
            Movie movie,
            HashSet<Guid> previousConflictIds)
        {
            if (movie.HasError || string.IsNullOrWhiteSpace(movie.TargetPath))
                return false;

            if (movie.ApprovedForCommit)
                return true;

            // DuplicateService deliberately sends equally ranked copies to review.
            // They still need proposed-path comparison so PIM can report the more
            // precise incoming collision instead of only "No clear best file".
            if (IsAmbiguousDuplicateReview(movie.ReviewReason))
                return true;

            // Re-evaluate prior conflict rows during dry-run/commit checks.
            return previousConflictIds.Contains(movie.Id);
        }

        private static bool IsAmbiguousDuplicateReview(string? reviewReason)
        {
            return !string.IsNullOrWhiteSpace(reviewReason) &&
                   reviewReason.StartsWith(
                       "No clear best file for ",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void SetDestinationConflict(
            Movie movie,
            string? message,
            string? existingPath)
        {
            movie.HasDestinationConflict = true;
            movie.DestinationConflictReason = string.IsNullOrWhiteSpace(message)
                ? "A destination conflict was detected."
                : message;
            movie.ExistingDestinationPath = existingPath;
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
                   (!movie.IsDuplicate ||
                    movie.KeepRecommended ||
                    movie.IsAlternateVersion);
        }

        private static bool IsConflictReviewReason(string? reviewReason)
        {
            if (string.IsNullOrWhiteSpace(reviewReason))
                return false;

            return reviewReason.Contains(
                       "Destination conflict:",
                       StringComparison.OrdinalIgnoreCase) ||
                   reviewReason.Contains(
                       "Plex library conflict:",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildMovieEditionIdentityKey(Movie movie)
        {
            var imdbId = movie.ImdbId?
                .Trim()
                .ToUpperInvariant() ?? string.Empty;

            return $"{imdbId}|{NormalizeEdition(movie.VersionTag)}";
        }

        private static string NormalizeEdition(string? edition)
        {
            if (string.IsNullOrWhiteSpace(edition) ||
                edition.Trim().Equals(
                    "Alternate Version",
                    StringComparison.OrdinalIgnoreCase))
            {
                return StandardEditionKey;
            }

            return edition.Trim().ToUpperInvariant();
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }
    }
}
