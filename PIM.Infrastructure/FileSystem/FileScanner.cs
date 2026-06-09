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
        private readonly string[] _videoExtensions =
        {
            ".mp4", ".mkv", ".avi", ".mov",
            ".wmv", ".flv", ".webm", ".mpg", ".mpeg",
            ".m4v", ".3gp", ".ts"
        };

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
        public List<Movie> Scan(string rootPath)
        {
            var movies = new List<Movie>();

            // 🔒 Safety check
            if (!Directory.Exists(rootPath))
                return movies;

            // 📂 Get all video files
            var files = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories)
                                 .Where(IsVideoFile);

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
        public List<string> GetFiles(string rootPath)
        {
            if (!Directory.Exists(rootPath))
                return new List<string>();

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };

            return Directory
                .EnumerateFiles(rootPath, "*.*", options)
                .Where(IsVideoFile)
                .Where(file => !IsInExcludedSystemFolder(file))
                .ToList();
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
        private bool IsVideoFile(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLower();
            return _videoExtensions.Contains(extension);
        }
    }
}