using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    /// <summary>
    /// Generates Plex-friendly movie paths and performs approved file moves.
    /// Organization folders are calculated by IDestinationPathBuilder so the
    /// same path is used by preview, conflict detection, and live commit.
    /// </summary>
    public class RenameService : IRenameService
    {
        private readonly IDestinationConflictService _destinationConflictService;
        private readonly IDestinationPathBuilder _destinationPathBuilder;

        public RenameService(
            IDestinationConflictService destinationConflictService,
            IDestinationPathBuilder destinationPathBuilder)
        {
            _destinationConflictService = destinationConflictService;
            _destinationPathBuilder = destinationPathBuilder;
        }

        public void GeneratePreview(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot)
        {
            if (movies == null || movies.Count == 0)
                return;

            ArgumentNullException.ThrowIfNull(profile);

            foreach (var movie in movies)
            {
                if (movie.HasError)
                {
                    ClearTarget(movie);
                    movie.ApprovedForCommit = false;
                    movie.Status = movie.ErrorMessage ??
                                   "Error requires attention before rename preview";
                    continue;
                }

                if (!IsValidForRename(movie))
                {
                    ClearTarget(movie);
                    movie.NeedsReview = true;
                    movie.ReviewReason ??= "Missing required metadata for rename";
                    movie.Status = movie.ReviewReason;
                    movie.ApprovedForCommit = false;
                    continue;
                }

                try
                {
                    var extension = GetSafeExtension(
                        movie.FileName ?? movie.OriginalFilePath);

                    var destination = _destinationPathBuilder.Build(
                        movie,
                        profile,
                        sourceRoot,
                        extension);

                    movie.NormalizedFolderName = destination.MovieFolderName;
                    movie.NormalizedFileName = destination.FileName;
                    movie.TargetPath = destination.FullFilePath;
                    movie.DestinationProfileId = profile.Id;
                    movie.DestinationProfileRevision = profile.Revision;

                    movie.ApprovedForCommit =
                        !movie.NeedsReview &&
                        (!movie.IsDuplicate ||
                         movie.KeepRecommended ||
                         movie.IsAlternateVersion);

                    movie.Status = movie.NeedsReview
                        ? movie.ReviewReason ?? "Review required before rename preview"
                        : destination.Warnings.Count == 0
                            ? "Rename preview generated"
                            : $"Rename preview generated with warning: {string.Join(" ", destination.Warnings)}";
                }
                catch (Exception ex)
                {
                    ClearTarget(movie);
                    movie.Status = "Rename error";
                    movie.ErrorMessage = ex.Message;
                    movie.ApprovedForCommit = false;
                }
            }
        }

        /// <summary>
        /// Simulates or performs approved moves. Live moves receive a fresh
        /// destination-conflict check immediately before each file operation.
        /// </summary>
        public void ExecuteChanges(
            List<Movie> movies,
            bool dryRun,
            string destinationRoot)
        {
            if (movies == null || movies.Count == 0)
                return;

            if (string.IsNullOrWhiteSpace(destinationRoot))
                throw new InvalidOperationException("Destination root is required.");

            var normalizedDestinationRoot = Path.GetFullPath(destinationRoot);

            foreach (var movie in movies)
            {
                try
                {
                    if (movie.NeedsReview ||
                        movie.HasError ||
                        !movie.ApprovedForCommit)
                    {
                        movie.ApprovedForCommit = false;

                        if (movie.HasError)
                        {
                            movie.Status = movie.ErrorMessage ??
                                           "Error requires attention before commit";
                        }
                        else if (movie.NeedsReview)
                        {
                            movie.Status = movie.ReviewReason ??
                                           "Review required before commit";
                        }
                        else
                        {
                            movie.Status = "Not approved for commit";
                        }

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(movie.TargetPath))
                    {
                        movie.Status = "Missing Target Path";
                        movie.ApprovedForCommit = false;
                        continue;
                    }

                    EnsureTargetIsUnderRoot(
                        normalizedDestinationRoot,
                        movie.TargetPath);

                    if (dryRun)
                    {
                        movie.Status = "Dry Run Complete";
                        continue;
                    }

                    if (!File.Exists(movie.OriginalFilePath))
                    {
                        movie.Status = "Source File Missing";
                        movie.ApprovedForCommit = false;
                        continue;
                    }

                    var destinationResult = _destinationConflictService.Check(
                        movie,
                        normalizedDestinationRoot,
                        refresh: true);

                    if (destinationResult.HasConflict)
                    {
                        MarkRuntimeDestinationConflict(movie, destinationResult);
                        continue;
                    }

                    var targetDirectory = Path.GetDirectoryName(movie.TargetPath);

                    if (!string.IsNullOrWhiteSpace(targetDirectory))
                        Directory.CreateDirectory(targetDirectory);

                    if (File.Exists(movie.TargetPath))
                    {
                        MarkRuntimeDestinationConflict(
                            movie,
                            DestinationConflictResult.Conflict(
                                DestinationConflictType.TargetFileAlreadyExists,
                                "The exact target file appeared before the move could be completed.",
                                movie.TargetPath));
                        continue;
                    }

                    File.Move(
                        movie.OriginalFilePath,
                        movie.TargetPath,
                        overwrite: false);

                    _destinationConflictService.InvalidateCache();
                    movie.Status = "Committed";
                }
                catch (IOException ex)
                {
                    if (!string.IsNullOrWhiteSpace(movie.TargetPath) &&
                        File.Exists(movie.TargetPath))
                    {
                        MarkRuntimeDestinationConflict(
                            movie,
                            DestinationConflictResult.Conflict(
                                DestinationConflictType.TargetFileAlreadyExists,
                                "The target file appeared while the move was being performed.",
                                movie.TargetPath));
                    }
                    else
                    {
                        movie.Status = "Error";
                        movie.ErrorMessage = ex.Message;
                        movie.ApprovedForCommit = false;
                    }
                }
                catch (Exception ex)
                {
                    movie.Status = "Error";
                    movie.ErrorMessage = ex.Message;
                    movie.ApprovedForCommit = false;
                }
            }
        }

        private static void EnsureTargetIsUnderRoot(
            string destinationRoot,
            string targetPath)
        {
            var root = Path.GetFullPath(destinationRoot)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

            var target = Path.GetFullPath(targetPath)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

            var rootPrefix = root + Path.DirectorySeparatorChar;

            if (!target.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The target path is outside the selected destination root.");
            }
        }

        private static void MarkRuntimeDestinationConflict(
            Movie movie,
            DestinationConflictResult result)
        {
            movie.HasDestinationConflict = true;
            movie.DestinationConflictReason = result.Message ??
                                              "A destination conflict was detected.";
            movie.ExistingDestinationPath = result.ExistingPath;
            movie.NeedsReview = true;
            movie.ApprovedForCommit = false;
            movie.Status = "Needs Review - Destination Changed";
            movie.ReviewReason =
                $"Destination conflict: {movie.DestinationConflictReason}";
        }

        private static bool IsValidForRename(Movie movie)
        {
            return !string.IsNullOrWhiteSpace(movie.Title) &&
                   movie.Year.HasValue &&
                   !string.IsNullOrWhiteSpace(movie.ImdbId);
        }

        private static void ClearTarget(Movie movie)
        {
            movie.NormalizedFolderName = null;
            movie.NormalizedFileName = null;
            movie.TargetPath = null;
            movie.DestinationProfileId = null;
            movie.DestinationProfileRevision = 0;
        }

        private static string GetSafeExtension(string? fileName)
        {
            return string.IsNullOrWhiteSpace(fileName)
                ? string.Empty
                : Path.GetExtension(fileName);
        }
    }
}
