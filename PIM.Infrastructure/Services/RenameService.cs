using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    /// <summary>
    /// Generates normalized Plex-friendly folder names, file names, and target paths.
    ///
    /// PIM's current naming strategy follows Plex's recommended movie folder structure:
    ///
    /// Normal movie:
    /// /Movies
    ///     /The Dark Knight (2008) {imdb-tt0468569}
    ///         The Dark Knight (2008) {imdb-tt0468569}.mkv
    ///
    /// Movie edition:
    /// /Movies
    ///     /The Dark Knight (2008) {imdb-tt0468569} {edition-IMAX}
    ///         The Dark Knight (2008) {imdb-tt0468569} {edition-IMAX}.mkv
    ///
    /// IMPORTANT:
    /// - This service generates rename previews and performs approved file moves.
    /// - Dry runs do NOT move files.
    /// - Edition tags are included in BOTH the folder name and file name.
    /// - Collections are NOT represented as physical folders. Plex collections should be handled later through Plex metadata/API work.
    ///
    /// OUTPUT FIELDS SET:
    /// - NormalizedFolderName
    /// - NormalizedFileName
    /// - TargetPath
    ///
    /// FUTURE EXTENSIONS:
    /// - Custom naming templates
    /// - Conflict detection for same target path collisions
    /// - Optional edition tag normalization rules
    /// - Additional path safety/sanitization
    /// </summary>
    public class RenameService : IRenameService
    {
        /// <summary>
        /// Generates preview paths for all valid movies.
        ///
        /// A movie is considered valid for renaming when it has:
        /// - Title
        /// - Year
        /// - IMDb ID
        ///
        /// Invalid movies are marked as not approved for commit.
        /// </summary>
        /// <param name="movies">Movies to process.</param>
        /// <param name="basePath">Root output directory where renamed movie folders will be created.</param>
        public void GeneratePreview(List<Movie> movies, string basePath)
        {
            if (movies == null || movies.Count == 0)
                return;

            foreach (var movie in movies)
            {
                // ---------------------------------------------------------
                // Safety guard: review/error rows are not rename-ready.
                //
                // PIM may still know enough to DISPLAY information about
                // these files, but it should not generate a final TargetPath
                // or mark them as rename-preview-ready until the review/error
                // state has been resolved.
                // ---------------------------------------------------------
                if (movie.NeedsReview || movie.HasError)
                {
                    movie.NormalizedFolderName = null;
                    movie.NormalizedFileName = null;
                    movie.TargetPath = null;
                    movie.ApprovedForCommit = false;

                    if (movie.HasError)
                    {
                        movie.Status = movie.ErrorMessage ?? "Error requires attention before rename preview";
                    }
                    else
                    {
                        movie.Status = movie.ReviewReason ?? "Review required before rename preview";
                    }

                    continue;
                }

                // ---------------------------------------------------------
                // Validate required metadata.
                //
                // PIM should not generate a target path unless we have the
                // minimum metadata needed for a clean Plex folder/file name.
                // ---------------------------------------------------------
                if (!IsValidForRename(movie))
                {
                    movie.NormalizedFolderName = null;
                    movie.NormalizedFileName = null;
                    movie.TargetPath = null;
                    movie.NeedsReview = true;
                    movie.ReviewReason ??= "Missing required metadata for rename";
                    movie.Status = movie.ReviewReason;
                    movie.ApprovedForCommit = false;
                    continue;
                }

                try
                {
                    // ---------------------------------------------------------
                    // Extract the original file extension safely.
                    //
                    // Example:
                    // Avatar.2009.Extended.Edition.mkv -> .mkv
                    // ---------------------------------------------------------
                    var extension = GetSafeExtension(movie.FileName);

                    // ---------------------------------------------------------
                    // Build the Plex movie base name.
                    //
                    // This is the shared naming core used for BOTH folder and file.
                    //
                    // Normal:
                    // Avatar (2009) {imdb-tt0499549}
                    //
                    // Edition:
                    // Avatar (2009) {imdb-tt0499549} {edition-Extended Edition}
                    //
                    // Plex recommends including edition information in both
                    // the folder name and file name for consistency.
                    // ---------------------------------------------------------
                    var movieBaseName = BuildMovieBaseName(movie);

                    // ---------------------------------------------------------
                    // Build final folder and file names.
                    //
                    // For Plex movie libraries, each movie item gets its own
                    // folder. Editions also get their own edition-specific folder.
                    // ---------------------------------------------------------
                    var folderName = movieBaseName;
                    var fileName = movieBaseName + extension;

                    // ---------------------------------------------------------
                    // Build the full target path.
                    //
                    // Example:
                    // D:\PIM_Output\
                    //     Avatar (2009) {imdb-tt0499549} {edition-Extended Edition}\
                    //         Avatar (2009) {imdb-tt0499549} {edition-Extended Edition}.mkv
                    // ---------------------------------------------------------
                    var targetPath = Path.Combine(basePath, folderName, fileName);

                    // ---------------------------------------------------------
                    // Store generated rename values on the movie object.
                    //
                    // The UI preview, dry-run preview, and live commit process
                    // all use these values.
                    // ---------------------------------------------------------
                    movie.NormalizedFolderName = folderName;
                    movie.NormalizedFileName = fileName;
                    movie.TargetPath = targetPath;

                    // ---------------------------------------------------------
                    // Default commit approval logic.
                    //
                    // A movie is approved when:
                    // - It does not need review
                    // - It is not a duplicate, OR
                    // - It is the recommended file to keep, OR
                    // - It is an alternate/edition version that should be kept
                    //
                    // This matters because different editions may share the same
                    // IMDb ID, but they should not be treated as throwaway duplicates.
                    // ---------------------------------------------------------
                    movie.ApprovedForCommit =
                        !movie.NeedsReview &&
                        (!movie.IsDuplicate || movie.KeepRecommended || movie.IsAlternateVersion);

                    movie.Status = "Rename preview generated";
                }
                catch (Exception ex)
                {
                    // ---------------------------------------------------------
                    // Capture unexpected rename-preview errors.
                    //
                    // Do not approve files that failed target path generation.
                    // ---------------------------------------------------------
                    movie.Status = "Rename error";
                    movie.ErrorMessage = ex.Message;
                    movie.ApprovedForCommit = false;
                }
            }
        }

        /// <summary>
        /// Executes the approved file operations.
        ///
        /// If dryRun is true:
        /// - No file system changes are made.
        /// - Movie status is updated to show that the dry run completed.
        ///
        /// If dryRun is false:
        /// - The target folder is created if needed.
        /// - The source file is moved to the generated target path.
        ///
        /// This method expects the caller to pass only approved movies.
        /// </summary>
        /// <param name="movies">Approved movies to process.</param>
        /// <param name="dryRun">When true, simulate changes without moving files.</param>
        public void ExecuteChanges(List<Movie> movies, bool dryRun)
        {
            if (movies == null || movies.Count == 0)
                return;

            foreach (var movie in movies)
            {
                try
                {
                    // ---------------------------------------------------------
                    // Final safety guard. Even if the caller accidentally passes
                    // a review/error row, RenameService must not move it.
                    // ---------------------------------------------------------
                    if (movie.NeedsReview || movie.HasError || !movie.ApprovedForCommit)
                    {
                        movie.ApprovedForCommit = false;

                        if (movie.HasError)
                        {
                            movie.Status = movie.ErrorMessage ?? "Error requires attention before commit";
                        }
                        else if (movie.NeedsReview)
                        {
                            movie.Status = movie.ReviewReason ?? "Review required before commit";
                        }
                        else
                        {
                            movie.Status = "Not approved for commit";
                        }

                        continue;
                    }

                    // ---------------------------------------------------------
                    // TargetPath must already have been generated by GeneratePreview.
                    // ---------------------------------------------------------
                    if (string.IsNullOrWhiteSpace(movie.TargetPath))
                    {
                        movie.Status = "Missing Target Path";
                        movie.ApprovedForCommit = false;
                        continue;
                    }

                    // ---------------------------------------------------------
                    // Dry run mode:
                    // Do not touch the file system.
                    // ---------------------------------------------------------
                    if (dryRun)
                    {
                        movie.Status = "Dry Run Complete";
                        continue;
                    }

                    // ---------------------------------------------------------
                    // Live commit mode:
                    // Create the target directory and move the file.
                    // ---------------------------------------------------------
                    var targetDirectory = Path.GetDirectoryName(movie.TargetPath);

                    if (!string.IsNullOrWhiteSpace(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    if (File.Exists(movie.OriginalFilePath))
                    {
                        File.Move(
                            movie.OriginalFilePath,
                            movie.TargetPath,
                            overwrite: false);

                        movie.Status = "Committed";
                    }
                    else
                    {
                        movie.Status = "Source File Missing";
                        movie.ApprovedForCommit = false;
                    }
                }
                catch (Exception ex)
                {
                    // ---------------------------------------------------------
                    // Capture file system errors such as:
                    // - Target file already exists
                    // - Permission denied
                    // - Invalid path characters
                    // - File locked by another process
                    // ---------------------------------------------------------
                    movie.Status = "Error";
                    movie.ErrorMessage = ex.Message;
                    movie.ApprovedForCommit = false;
                }
            }
        }

        // =========================================================
        // Helper Methods
        // =========================================================

        /// <summary>
        /// Determines whether a movie has the minimum metadata required
        /// to generate a Plex-friendly rename target.
        /// </summary>
        private bool IsValidForRename(Movie movie)
        {
            return !string.IsNullOrWhiteSpace(movie.Title)
                && movie.Year.HasValue
                && !string.IsNullOrWhiteSpace(movie.ImdbId);
        }

        /// <summary>
        /// Builds the base Plex movie name used for both folder and file names.
        ///
        /// Normal movie:
        /// The Dark Knight (2008) {imdb-tt0468569}
        ///
        /// Edition movie:
        /// The Dark Knight (2008) {imdb-tt0468569} {edition-IMAX}
        ///
        /// The IMDb ID helps Plex match the movie accurately.
        /// The edition tag helps Plex distinguish alternate versions of the same movie.
        /// </summary>
        private string BuildMovieBaseName(Movie movie)
        {
            var baseName = $"{movie.Title} ({movie.Year}) {{imdb-{movie.ImdbId}}}";

            if (!string.IsNullOrWhiteSpace(movie.VersionTag))
            {
                baseName += $" {{edition-{movie.VersionTag}}}";
            }

            return baseName;
        }

        /// <summary>
        /// Safely extracts the file extension from the original filename.
        ///
        /// If the filename is missing, this returns an empty string instead
        /// of throwing an exception.
        /// </summary>
        private string GetSafeExtension(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            return Path.GetExtension(fileName);
        }
    }
}