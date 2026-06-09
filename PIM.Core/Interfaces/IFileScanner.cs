using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    /// <summary>
    /// Defines operations for discovering and preparing movie files from the file system.
    ///
    /// RESPONSIBILITIES:
    /// - Locate video files on disk
    /// - Create basic Movie objects
    /// - Delegate filename parsing to IFileNameParser
    ///
    /// DOES NOT:
    /// - Perform metadata enrichment (OMDb, etc.)
    /// - Detect duplicates
    /// - Generate rename/move output
    ///
    /// This separation keeps the scanning process fast, testable, and reusable.
    /// </summary>
    public interface IFileScanner
    {
        /// <summary>
        /// Performs a full scan of the directory and returns parsed Movie objects.
        ///
        /// Use this method for:
        /// - Simple scans
        /// - Testing
        /// - Non-progress scenarios
        ///
        /// Internally:
        /// - Discovers files
        /// - Creates Movie objects
        /// - Calls IFileNameParser to extract title/year
        /// </summary>
        /// <param name="rootPath">Root directory to scan</param>
        /// <returns>List of parsed Movie objects</returns>
        List<Movie> Scan(string rootPath);

        /// <summary>
        /// Returns ONLY file paths for valid video files.
        ///
        /// This is used for:
        /// - Progress tracking (knowing total file count)
        /// - Incremental processing (one file at a time)
        /// - Background processing scenarios
        ///
        /// IMPORTANT:
        /// This method does NOT create Movie objects or perform parsing.
        /// </summary>
        /// <param name="rootPath">Root directory to scan</param>
        /// <returns>List of video file paths</returns>
        List<string> GetFiles(string rootPath);
    }
}