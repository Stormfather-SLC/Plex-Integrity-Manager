using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.FileSystem
{
    /// <summary>
    /// Responsible ONLY for:
    /// - Discovering video files on disk
    /// - Creating basic Movie objects
    /// - Delegating filename parsing to IFileNameParser
    ///
    /// DOES NOT:
    /// - Call external APIs (OMDb)
    /// - Detect duplicates
    /// - Generate rename output
    /// </summary>
    public class FileScanner : IFileScanner
    {
        private readonly IFileNameParser _parser;

        /// <summary>
        /// List of supported video file extensions.
        /// IMPORTANT: Keep this centralized so filtering is consistent everywhere.
        /// </summary>
        private static readonly HashSet<string> VideoExtensions = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov",
            ".wmv", ".flv", ".webm", ".mpg", ".mpeg",
            ".m4v", ".3gp", ".ts"
        };

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        public FileScanner(IFileNameParser parser)
        {
            _parser = parser;
        }

        /// <summary>
        /// Scans the directory and returns fully parsed Movie objects.
        ///
        /// This is the "all-in-one" method (used before progress tracking).
        /// You can still use this for quick scans or testing.
        /// </summary>
        public List<Movie> Scan(
            string rootPath,
            string? excludedRootPath = null)
        {
            var movies = new List<Movie>();

            var files = GetFiles(rootPath, excludedRootPath);

            foreach (var file in files)
            {
                var movie = CreateBasicMovie(file);

                // 🧠 Delegate parsing (title/year extraction)
                _parser.Parse(movie);

                movies.Add(movie);
            }

            return movies;
        }

        /// <summary>
        /// Returns ONLY the file paths.
        ///
        /// This is used for progress tracking:
        /// Step 1: Get total file count
        /// Step 2: Process each file individually
        /// </summary>
        public List<string> GetFiles(
            string rootPath,
            string? excludedRootPath = null)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return new List<string>();

            string sourceRoot;

            try
            {
                sourceRoot = NormalizePath(rootPath);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException)
            {
                return new List<string>();
            }

            if (!Directory.Exists(sourceRoot))
                return new List<string>();

            var excludedRoot = NormalizeExcludedRoot(
                sourceRoot,
                excludedRootPath);
            var pendingDirectories = new Stack<string>();
            var visitedDirectories = new HashSet<string>(PathComparer);
            var discoveredFiles = new HashSet<string>(PathComparer);

            pendingDirectories.Push(sourceRoot);

            while (pendingDirectories.Count > 0)
            {
                var currentDirectory = pendingDirectories.Pop();

                if (!visitedDirectories.Add(currentDirectory) ||
                    IsSameOrDescendant(currentDirectory, excludedRoot))
                {
                    continue;
                }

                foreach (var file in EnumerateFilesSafely(currentDirectory))
                {
                    var fullPath = Path.GetFullPath(file);

                    if (IsVideoFile(fullPath) &&
                        !IsInExcludedSystemFolder(fullPath) &&
                        !IsSameOrDescendant(fullPath, excludedRoot))
                    {
                        discoveredFiles.Add(fullPath);
                    }
                }

                foreach (var directory in EnumerateDirectoriesSafely(
                             currentDirectory))
                {
                    var fullPath = Path.GetFullPath(directory);

                    if (IsSameOrDescendant(fullPath, excludedRoot) ||
                        IsInExcludedSystemFolder(fullPath) ||
                        IsReparsePoint(fullPath))
                    {
                        continue;
                    }

                    pendingDirectories.Push(fullPath);
                }
            }

            return discoveredFiles
                .OrderBy(path => path, PathComparer)
                .ToList();
        }

        private static IEnumerable<string> EnumerateFilesSafely(
            string directory)
        {
            try
            {
                return Directory.GetFiles(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> EnumerateDirectoriesSafely(
            string directory)
        {
            try
            {
                return Directory.GetDirectories(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        private static bool IsReparsePoint(string directory)
        {
            try
            {
                return (File.GetAttributes(directory) &
                        FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                // A directory whose attributes cannot be verified is unsafe to
                // traverse. Fail closed and leave it out of the scan.
                return true;
            }
        }

        private static string? NormalizeExcludedRoot(
            string sourceRoot,
            string? excludedRootPath)
        {
            if (string.IsNullOrWhiteSpace(excludedRootPath))
                return null;

            try
            {
                var excludedRoot = NormalizePath(excludedRootPath);
                return IsSameOrDescendant(excludedRoot, sourceRoot)
                    ? excludedRoot
                    : null;
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            var fullPath = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(fullPath);

            return string.Equals(fullPath, root, PathComparison)
                ? fullPath
                : fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static bool IsSameOrDescendant(
            string path,
            string? rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return false;

            if (string.Equals(path, rootPath, PathComparison))
                return true;

            var rootWithSeparator = rootPath.EndsWith(
                Path.DirectorySeparatorChar)
                ? rootPath
                : rootPath + Path.DirectorySeparatorChar;

            return path.StartsWith(rootWithSeparator, PathComparison);
        }

        private static bool IsInExcludedSystemFolder(string path)
        {
            var normalized = path.Replace('/', '\\');

            return normalized.Contains(@"\System Volume Information", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(@"\$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(@"\Recovery", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a base Movie object from a file path.
        ///
        /// This keeps object creation consistent between Scan() and progress-based scanning.
        /// </summary>
        private Movie CreateBasicMovie(string filePath)
        {
            var fileInfo = new FileInfo(filePath);

            return new Movie
            {
                OriginalFilePath = filePath,
                FileName = fileInfo.Name,
                DirectoryPath = fileInfo.DirectoryName,
                FileSizeBytes = fileInfo.Length,
                Status = "Discovered"
            };
        }

        /// <summary>
        /// Determines if a file is a supported video format.
        ///
        /// Centralized logic prevents mismatches between Scan() and GetFiles().
        /// </summary>
        private static bool IsVideoFile(string filePath)
        {
            return VideoExtensions.Contains(Path.GetExtension(filePath));
        }
    }
}
