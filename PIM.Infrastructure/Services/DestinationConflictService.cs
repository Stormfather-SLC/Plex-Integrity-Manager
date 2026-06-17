using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    public class DestinationConflictService : IDestinationConflictService
    {
        private string? _cachedOutputPath;
        private List<string> _cachedDestinationEntries = new();

        public DestinationConflictResult Check(Movie movie, string outputPath)
        {
            if (movie == null)
                return DestinationConflictResult.NoConflict();

            if (string.IsNullOrWhiteSpace(outputPath))
                return DestinationConflictResult.NoConflict();

            if (string.IsNullOrWhiteSpace(movie.TargetPath))
                return DestinationConflictResult.NoConflict();

            var targetPath = Path.GetFullPath(movie.TargetPath);

            if (PathsEqual(targetPath, movie.OriginalFilePath))
                return DestinationConflictResult.NoConflict();

            if (File.Exists(targetPath))
            {
                return DestinationConflictResult.Conflict(
                    DestinationConflictType.TargetFileAlreadyExists,
                    "Target file already exists.",
                    targetPath);
            }

            var targetDirectory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrWhiteSpace(targetDirectory) &&
                Directory.Exists(targetDirectory))
            {
                return DestinationConflictResult.Conflict(
                    DestinationConflictType.TargetFolderAlreadyExists,
                    "Target folder already exists.",
                    targetDirectory);
            }

            if (!Directory.Exists(outputPath))
                return DestinationConflictResult.NoConflict();

            var destinationEntries = GetDestinationEntries(outputPath);

            if (!string.IsNullOrWhiteSpace(movie.ImdbId))
            {
                var imdbToken = $"{{imdb-{movie.ImdbId}}}";

                var sameImdbMatch = destinationEntries.FirstOrDefault(path =>
                    path.Contains(imdbToken, StringComparison.OrdinalIgnoreCase) &&
                    !PathsEqual(path, movie.OriginalFilePath) &&
                    !PathsEqual(path, targetPath));

                if (!string.IsNullOrWhiteSpace(sameImdbMatch))
                {
                    return DestinationConflictResult.Conflict(
                        DestinationConflictType.SameImdbIdExistsInDestination,
                        "Destination library already contains this IMDb ID.",
                        sameImdbMatch);
                }
            }

            if (!string.IsNullOrWhiteSpace(movie.Title) && movie.Year.HasValue)
            {
                var titleYearToken = $"{movie.Title} ({movie.Year})";

                var similarTitleYearMatch = destinationEntries.FirstOrDefault(path =>
                {
                    var name = Path.GetFileName(path);

                    return name.Contains(titleYearToken, StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("{imdb-", StringComparison.OrdinalIgnoreCase);
                });

                if (!string.IsNullOrWhiteSpace(similarTitleYearMatch))
                {
                    return DestinationConflictResult.Conflict(
                        DestinationConflictType.SimilarTitleYearExistsInDestination,
                        "Destination library contains a similar Title + Year item without an IMDb ID.",
                        similarTitleYearMatch);
                }
            }

            return DestinationConflictResult.NoConflict();
        }

        private List<string> GetDestinationEntries(string outputPath)
        {
            var normalizedOutputPath = Path.GetFullPath(outputPath);

            if (_cachedOutputPath != null &&
                PathsEqual(_cachedOutputPath, normalizedOutputPath))
            {
                return _cachedDestinationEntries;
            }

            _cachedOutputPath = normalizedOutputPath;
            _cachedDestinationEntries = SafeEnumerateFileSystemEntries(normalizedOutputPath).ToList();

            return _cachedDestinationEntries;
        }

        private static IEnumerable<string> SafeEnumerateFileSystemEntries(string rootPath)
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootPath);

            while (pendingDirectories.Count > 0)
            {
                var currentDirectory = pendingDirectories.Pop();

                IEnumerable<string> childDirectories;

                try
                {
                    childDirectories = Directory.EnumerateDirectories(currentDirectory).ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var childDirectory in childDirectories)
                {
                    yield return childDirectory;
                    pendingDirectories.Push(childDirectory);
                }

                IEnumerable<string> childFiles;

                try
                {
                    childFiles = Directory.EnumerateFiles(currentDirectory).ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var childFile in childFiles)
                {
                    yield return childFile;
                }
            }
        }

        private static bool PathsEqual(string? firstPath, string? secondPath)
        {
            if (string.IsNullOrWhiteSpace(firstPath) ||
                string.IsNullOrWhiteSpace(secondPath))
            {
                return false;
            }

            var firstFullPath = Path.GetFullPath(firstPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var secondFullPath = Path.GetFullPath(secondPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(
                firstFullPath,
                secondFullPath,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}