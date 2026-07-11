using System.Text.RegularExpressions;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    public class DestinationConflictService : IDestinationConflictService
    {
        private const string StandardEditionKey = "<standard>";

        private static readonly Regex EditionRegex = new(
            @"\{edition-(?<edition>[^}]+)\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string? _cachedOutputPath;
        private List<string> _cachedDestinationEntries = new();

        public void InvalidateCache()
        {
            _cachedOutputPath = null;
            _cachedDestinationEntries = new List<string>();
        }

        public DestinationConflictResult Check(
            Movie movie,
            string outputPath,
            bool refresh = false)
        {
            if (movie == null ||
                string.IsNullOrWhiteSpace(outputPath) ||
                string.IsNullOrWhiteSpace(movie.TargetPath))
            {
                return DestinationConflictResult.NoConflict();
            }

            string targetPath;

            try
            {
                targetPath = Path.GetFullPath(movie.TargetPath);
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException)
            {
                return DestinationConflictResult.Conflict(
                    DestinationConflictType.InvalidTargetPath,
                    $"The proposed target path is invalid: {ex.Message}",
                    movie.TargetPath);
            }

            if (PathsEqual(targetPath, movie.OriginalFilePath))
                return DestinationConflictResult.NoConflict();

            if (File.Exists(targetPath))
            {
                return DestinationConflictResult.Conflict(
                    DestinationConflictType.TargetFileAlreadyExists,
                    "The exact target file already exists.",
                    targetPath);
            }

            var targetDirectory = Path.GetDirectoryName(targetPath);

            // An existing directory is expected when another edition of the
            // same movie is already present. Only a file occupying the folder
            // path should block the operation.
            if (!string.IsNullOrWhiteSpace(targetDirectory) &&
                File.Exists(targetDirectory))
            {
                return DestinationConflictResult.Conflict(
                    DestinationConflictType.TargetDirectoryBlockedByFile,
                    "A file exists where the target movie folder must be created.",
                    targetDirectory);
            }

            if (!Directory.Exists(outputPath))
                return DestinationConflictResult.NoConflict();

            var destinationEntries = GetDestinationEntries(outputPath, refresh);

            if (!string.IsNullOrWhiteSpace(movie.ImdbId))
            {
                var imdbToken = $"{{imdb-{movie.ImdbId}}}";
                var incomingEdition = NormalizeEdition(movie.VersionTag);

                var sameEditionMatch = destinationEntries
                    .Where(File.Exists)
                    .FirstOrDefault(path =>
                    {
                        if (PathsEqual(path, movie.OriginalFilePath) ||
                            PathsEqual(path, targetPath))
                        {
                            return false;
                        }

                        if (!path.Contains(
                                imdbToken,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        var existingEdition = GetEditionKeyFromPath(path);

                        return string.Equals(
                            existingEdition,
                            incomingEdition,
                            StringComparison.OrdinalIgnoreCase);
                    });

                if (!string.IsNullOrWhiteSpace(sameEditionMatch))
                {
                    var editionDescription = incomingEdition == StandardEditionKey
                        ? "the standard edition"
                        : $"edition '{movie.VersionTag}'";

                    return DestinationConflictResult.Conflict(
                        DestinationConflictType.SameImdbIdAndEditionExistsInDestination,
                        $"The destination already contains the same IMDb ID and {editionDescription}.",
                        sameEditionMatch);
                }
            }

            if (!string.IsNullOrWhiteSpace(movie.Title) && movie.Year.HasValue)
            {
                var titleYearToken = $"{movie.Title} ({movie.Year})";

                var similarTitleYearMatch = destinationEntries.FirstOrDefault(path =>
                {
                    if (PathsEqual(path, movie.OriginalFilePath) ||
                        PathsEqual(path, targetPath))
                    {
                        return false;
                    }

                    var name = Path.GetFileName(path);

                    return name.Contains(
                               titleYearToken,
                               StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains(
                               "{imdb-",
                               StringComparison.OrdinalIgnoreCase);
                });

                if (!string.IsNullOrWhiteSpace(similarTitleYearMatch))
                {
                    return DestinationConflictResult.Conflict(
                        DestinationConflictType.SimilarTitleYearExistsInDestination,
                        "The destination contains a similar title and year without an IMDb ID.",
                        similarTitleYearMatch);
                }
            }

            return DestinationConflictResult.NoConflict();
        }

        private List<string> GetDestinationEntries(
            string outputPath,
            bool refresh)
        {
            string normalizedOutputPath;

            try
            {
                normalizedOutputPath = Path.GetFullPath(outputPath);
            }
            catch
            {
                return new List<string>();
            }

            if (!refresh &&
                _cachedOutputPath != null &&
                PathsEqual(_cachedOutputPath, normalizedOutputPath))
            {
                return _cachedDestinationEntries;
            }

            _cachedOutputPath = normalizedOutputPath;
            _cachedDestinationEntries = Directory.Exists(normalizedOutputPath)
                ? SafeEnumerateFileSystemEntries(normalizedOutputPath).ToList()
                : new List<string>();

            return _cachedDestinationEntries;
        }

        private static string GetEditionKeyFromPath(string path)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var match = EditionRegex.Match(fileName);

            return match.Success
                ? NormalizeEdition(match.Groups["edition"].Value)
                : StandardEditionKey;
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

            return Regex.Replace(edition.Trim(), @"\s+", " ")
                .ToUpperInvariant();
        }

        private static IEnumerable<string> SafeEnumerateFileSystemEntries(
            string rootPath)
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootPath);

            while (pendingDirectories.Count > 0)
            {
                var currentDirectory = pendingDirectories.Pop();

                IEnumerable<string> childDirectories;

                try
                {
                    childDirectories = Directory
                        .EnumerateDirectories(currentDirectory)
                        .ToList();
                }
                catch
                {
                    childDirectories = Array.Empty<string>();
                }

                foreach (var childDirectory in childDirectories)
                {
                    yield return childDirectory;
                    pendingDirectories.Push(childDirectory);
                }

                IEnumerable<string> childFiles;

                try
                {
                    childFiles = Directory
                        .EnumerateFiles(currentDirectory)
                        .ToList();
                }
                catch
                {
                    childFiles = Array.Empty<string>();
                }

                foreach (var childFile in childFiles)
                {
                    yield return childFile;
                }
            }
        }

        private static bool PathsEqual(
            string? firstPath,
            string? secondPath)
        {
            if (string.IsNullOrWhiteSpace(firstPath) ||
                string.IsNullOrWhiteSpace(secondPath))
            {
                return false;
            }

            try
            {
                var firstFullPath = Path.GetFullPath(firstPath)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);

                var secondFullPath = Path.GetFullPath(secondPath)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);

                return string.Equals(
                    firstFullPath,
                    secondFullPath,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
