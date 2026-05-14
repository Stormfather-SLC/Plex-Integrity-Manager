using PIM.Core.Models;
using PIM.Web.Models;

namespace PIM.Web.Services
{
    /// <summary>
    /// Builds a virtual preview tree from movie TargetPath values.
    /// This is used to show the user what the output folder/file structure
    /// will look like before any files are moved.
    /// </summary>
    public class PreviewTreeService
    {
        public FileNode BuildTree(List<Movie> movies, string basePath)
        {
            var root = new FileNode
            {
                Name = basePath,
                IsFolder = true,
                IsExpanded = true
            };

            foreach (var movie in movies)
            {
                // 🎯 Only include movies that are approved to keep
                var shouldKeep =
                    !movie.HasError &&
                    !movie.NeedsReview &&
                    (
                        movie.KeepRecommended ||
                        movie.IsAlternateVersion ||
                        !movie.IsDuplicate
                    );

                if (!shouldKeep)
                    continue;

                if (string.IsNullOrWhiteSpace(movie.TargetPath))
                    continue;

                var relativePath = movie.TargetPath
                    .Replace(basePath, "")
                    .TrimStart(Path.DirectorySeparatorChar);

                var parts = relativePath.Split(
                    Path.DirectorySeparatorChar,
                    StringSplitOptions.RemoveEmptyEntries);

                AddToTree(root, parts, movie);
            }

            // IMPORTANT:
            // Count files AFTER the tree is built.
            // This prevents double-counting or missed counts.
            CalculateFileCounts(root);

            return root;
        }

        private void AddToTree(FileNode root, string[] parts, Movie movie)
        {
            var current = root;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];

                // The last part is the file name.
                var isFile = i == parts.Length - 1;

                var existing = current.Children
                    .FirstOrDefault(c => c.Name == part);

                if (existing == null)
                {
                    existing = new FileNode
                    {
                        Name = part,
                        IsFolder = !isFile,

                        // 👇 THIS is the critical fix
                        Movie = isFile ? movie : null,

                        // Existing metadata (optional now, but fine to keep)
                        IsDuplicate = isFile && movie.IsDuplicate,
                        KeepRecommended = isFile && movie.KeepRecommended,
                        Status = isFile ? movie.Status : null
                    };

                    current.Children.Add(existing);
                }
                else if (isFile)
                {
                    // 👇 IMPORTANT: ensures existing nodes also get the movie
                    existing.Movie = movie;
                }

                current = existing;
            }
        }

        /// <summary>
        /// Recursively calculates how many files exist under each folder.
        ///
        /// Example:
        /// Avatar folder
        ///   Avatar.mp4
        ///   Avatar.mkv
        ///
        /// FileCount = 2
        /// </summary>
        private int CalculateFileCounts(FileNode node)
        {
            if (!node.IsFolder)
                return 1;

            var count = 0;

            foreach (var child in node.Children)
            {
                count += CalculateFileCounts(child);
            }

            node.FileCount = count;
            return count;
        }
    }
}