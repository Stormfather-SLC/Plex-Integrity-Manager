using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    /// <summary>
    /// Generates Plex-friendly movie paths and performs approved file moves.
    ///
    /// All editions of one movie share a parent folder:
    /// /Movies
    ///     /The Dark Knight (2008) {imdb-tt0468569}
    ///         The Dark Knight (2008) {imdb-tt0468569}.mkv
    ///         The Dark Knight (2008) {edition-IMAX} {imdb-tt0468569}.mkv
    /// </summary>
    public class RenameService : IRenameService
    {
        private readonly IDestinationConflictService _destinationConflictService;

        public RenameService(
            IDestinationConflictService destinationConflictService)
        {
            _destinationConflictService = destinationConflictService;
        }

        /// <summary>
        /// Generates proposed target paths whenever the required metadata exists.
        /// Review rows remain unapproved, but their proposed paths are retained so
        /// same-batch destination collisions can still be detected and explained.
        /// </summary>
        public void GeneratePreview(List<Movie> movies, string basePath)
        {
            if (movies == null || movies.Count == 0)
                return;

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

                    // Movie owns the canonical naming rules so the preview,
                    // conflict detector, and commit process stay consistent.
                    var folderName = movie.GetNormalizedFolderName();
                    var fileName = movie.GetNormalizedFileName(extension);
                    var targetPath = Path.Combine(basePath, folderName, fileName);

                    movie.NormalizedFolderName = folderName;
                    movie.NormalizedFileName = fileName;
                    movie.TargetPath = targetPath;

                    movie.ApprovedForCommit =
                        !movie.NeedsReview &&
                        (!movie.IsDuplicate ||
                         movie.KeepRecommended ||
                         movie.IsAlternateVersion);

                    movie.Status = movie.NeedsReview
                        ? movie.ReviewReason ?? "Review required before rename preview"
                        : "Rename preview generated";
                }
                catch (Exception ex)
                {
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
        public void ExecuteChanges(List<Movie> movies, bool dryRun)
        {
            if (movies == null || movies.Count == 0)
                return;

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

                    // Derive the root from the actual proposed TargetPath rather
                    // than configuration, so the final check always protects the
                    // exact location where this file is about to be moved.
                    var outputPath = ResolveOutputPath(movie.TargetPath);

                    var destinationResult = _destinationConflictService.Check(
                        movie,
                        outputPath,
                        refresh: true);

                    if (destinationResult.HasConflict)
                    {
                        MarkRuntimeDestinationConflict(movie, destinationResult);
                        continue;
                    }

                    var targetDirectory = Path.GetDirectoryName(movie.TargetPath);

                    if (!string.IsNullOrWhiteSpace(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    // Close the small race between the full destination scan
                    // and the move itself. File.Move also keeps overwrite off.
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

        private static string ResolveOutputPath(string targetPath)
        {
            var movieFolder = Path.GetDirectoryName(Path.GetFullPath(targetPath));

            if (string.IsNullOrWhiteSpace(movieFolder))
                return string.Empty;

            return Directory.GetParent(movieFolder)?.FullName ?? movieFolder;
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
        }

        private static string GetSafeExtension(string? fileName)
        {
            return string.IsNullOrWhiteSpace(fileName)
                ? string.Empty
                : Path.GetExtension(fileName);
        }
    }
}
