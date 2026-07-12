using Microsoft.Extensions.Configuration;
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
        private readonly IConfiguration _configuration;
        private readonly ScanProgress _progress;
        private readonly SourceCleanupStatus _sourceCleanupStatus;

        public RenameService(
            IDestinationConflictService destinationConflictService,
            IDestinationPathBuilder destinationPathBuilder,
            IConfiguration configuration,
            ScanProgress progress,
            SourceCleanupStatus sourceCleanupStatus)
        {
            _destinationConflictService = destinationConflictService;
            _destinationPathBuilder = destinationPathBuilder;
            _configuration = configuration;
            _progress = progress;
            _sourceCleanupStatus = sourceCleanupStatus;
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
        /// Simulates or performs approved moves. The destination snapshot created
        /// immediately before commit is reused for the full batch rather than
        /// recursively rescanning the destination library once per movie.
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
            var operationName = dryRun ? "Dry Run" : "Live Commit";
            var removeEmptySourceFolders =
                !dryRun &&
                _configuration.GetValue(
                    "PIM:RemoveEmptySourceFolders",
                    true);
            var committedSourceDirectories = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            _sourceCleanupStatus.Reset(removeEmptySourceFolders);

            _progress.Operation = operationName;
            _progress.Total = movies.Count;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;
            _progress.Message = dryRun
                ? "Simulating approved file operations..."
                : "Preparing approved file moves...";
            _progress.IsRunning = true;

            Console.WriteLine(
                $"[PIM] {operationName} started for {movies.Count:N0} approved file(s). " +
                $"Destination: {normalizedDestinationRoot}");

            try
            {
                foreach (var movie in movies)
                {
                    var displayName = movie.FileName ??
                                      Path.GetFileName(movie.OriginalFilePath) ??
                                      movie.Title ??
                                      "Unknown movie";

                    _progress.CurrentFile = displayName;
                    _progress.Message = dryRun
                        ? "Simulating move and rename..."
                        : "Checking the final destination and moving the file...";

                    Console.WriteLine(
                        $"[PIM] {(dryRun ? "Dry run" : "Processing")}: {displayName}");

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

                            Console.WriteLine(
                                $"[PIM] Skipped: {displayName} - {movie.Status}");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(movie.TargetPath))
                        {
                            movie.Status = "Missing Target Path";
                            movie.ApprovedForCommit = false;
                            Console.WriteLine(
                                $"[PIM] Skipped: {displayName} - missing target path.");
                            continue;
                        }

                        EnsureTargetIsUnderRoot(
                            normalizedDestinationRoot,
                            movie.TargetPath);

                        if (dryRun)
                        {
                            movie.Status = "Dry Run Complete";
                            Console.WriteLine(
                                $"[PIM] Would move: {movie.OriginalFilePath} -> {movie.TargetPath}");
                            continue;
                        }

                        if (!File.Exists(movie.OriginalFilePath))
                        {
                            movie.Status = "Source File Missing";
                            movie.ApprovedForCommit = false;
                            Console.WriteLine(
                                $"[PIM] Skipped: source file missing - {movie.OriginalFilePath}");
                            continue;
                        }

                        // ApplyConflictDetection has just built a fresh destination
                        // snapshot. Reuse it here; exact File.Exists checks below
                        // still protect against a target appearing during commit.
                        var destinationResult = _destinationConflictService.Check(
                            movie,
                            normalizedDestinationRoot,
                            refresh: false);

                        if (destinationResult.HasConflict)
                        {
                            MarkRuntimeDestinationConflict(movie, destinationResult);
                            Console.WriteLine(
                                $"[PIM] Conflict: {displayName} - {destinationResult.Message}");
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
                            Console.WriteLine(
                                $"[PIM] Conflict: target appeared before move - {movie.TargetPath}");
                            continue;
                        }

                        Console.WriteLine(
                            $"[PIM] Moving: {movie.OriginalFilePath} -> {movie.TargetPath}");

                        var originalDirectory = Path.GetDirectoryName(
                            movie.OriginalFilePath);

                        File.Move(
                            movie.OriginalFilePath,
                            movie.TargetPath,
                            overwrite: false);

                        if (!string.IsNullOrWhiteSpace(originalDirectory))
                            committedSourceDirectories.Add(originalDirectory);

                        _destinationConflictService.RecordDestinationEntry(movie.TargetPath);
                        movie.Status = "Committed";

                        Console.WriteLine($"[PIM] Committed: {movie.TargetPath}");
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

                        Console.WriteLine(
                            $"[PIM] File operation error for {displayName}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        movie.Status = "Error";
                        movie.ErrorMessage = ex.Message;
                        movie.ApprovedForCommit = false;

                        Console.WriteLine(
                            $"[PIM] Error processing {displayName}: {ex.Message}");
                    }
                    finally
                    {
                        _progress.Processed++;
                    }
                }

                if (removeEmptySourceFolders)
                {
                    _progress.CurrentFile = string.Empty;
                    _progress.Message = "Removing empty source folders...";

                    var cleanupResult = RemoveEmptyCommittedSourceFolders(
                        _configuration["PIM:ScanPath"] ?? string.Empty,
                        committedSourceDirectories);

                    _sourceCleanupStatus.Complete(
                        cleanupResult.EmptyFoldersRemoved,
                        cleanupResult.WarningCount);

                    Console.WriteLine(
                        $"[PIM] Source cleanup finished. " +
                        $"Removed {cleanupResult.EmptyFoldersRemoved:N0} empty folder(s); " +
                        $"warnings: {cleanupResult.WarningCount:N0}.");
                }
            }
            finally
            {
                _progress.CurrentFile = string.Empty;
                _progress.Message = dryRun
                    ? "Dry run complete."
                    : "Live commit complete.";
                _progress.IsRunning = false;

                Console.WriteLine(
                    $"[PIM] {operationName} finished. " +
                    $"Processed {_progress.Processed:N0} of {_progress.Total:N0} file(s).");
            }
        }

        private static SourceCleanupResult RemoveEmptyCommittedSourceFolders(
            string sourceRoot,
            IEnumerable<string> committedSourceDirectories)
        {
            var warningCount = 0;
            var removedCount = 0;

            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                Console.WriteLine(
                    "[PIM] Source cleanup warning: the configured source root is empty.");
                return new SourceCleanupResult(0, 1);
            }

            string normalizedSourceRoot;

            try
            {
                normalizedSourceRoot = NormalizeDirectoryPath(sourceRoot);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[PIM] Source cleanup warning: invalid source root - {ex.Message}");
                return new SourceCleanupResult(0, 1);
            }

            var sourceRootPrefix =
                normalizedSourceRoot + Path.DirectorySeparatorChar;
            var candidates = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var sourceDirectory in committedSourceDirectories)
            {
                if (string.IsNullOrWhiteSpace(sourceDirectory))
                    continue;

                try
                {
                    var current = NormalizeDirectoryPath(sourceDirectory);

                    if (!current.StartsWith(
                            sourceRootPrefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        warningCount++;
                        Console.WriteLine(
                            $"[PIM] Source cleanup warning: folder is outside the configured source root - {current}");
                        continue;
                    }

                    while (!string.Equals(
                               current,
                               normalizedSourceRoot,
                               StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(current);

                        var parent = Directory.GetParent(current)?.FullName;

                        if (string.IsNullOrWhiteSpace(parent))
                            break;

                        current = NormalizeDirectoryPath(parent);

                        if (!current.StartsWith(
                                sourceRootPrefix,
                                StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(
                                current,
                                normalizedSourceRoot,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    warningCount++;
                    Console.WriteLine(
                        $"[PIM] Source cleanup warning for '{sourceDirectory}': {ex.Message}");
                }
            }

            foreach (var directory in candidates
                         .OrderByDescending(path => path.Length))
            {
                try
                {
                    if (!Directory.Exists(directory))
                        continue;

                    var attributes = File.GetAttributes(directory);

                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Console.WriteLine(
                            $"[PIM] Source cleanup skipped reparse point: {directory}");
                        continue;
                    }

                    // EnumerateFileSystemEntries includes hidden files, metadata,
                    // subtitles, artwork, and remaining subfolders. Nothing is
                    // removed unless the directory is genuinely empty.
                    if (Directory.EnumerateFileSystemEntries(directory).Any())
                        continue;

                    Directory.Delete(directory, recursive: false);
                    removedCount++;

                    Console.WriteLine(
                        $"[PIM] Removed empty source folder: {directory}");
                }
                catch (Exception ex)
                {
                    warningCount++;
                    Console.WriteLine(
                        $"[PIM] Source cleanup warning for '{directory}': {ex.Message}");
                }
            }

            return new SourceCleanupResult(removedCount, warningCount);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
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

        private sealed record SourceCleanupResult(
            int EmptyFoldersRemoved,
            int WarningCount);
    }
}
