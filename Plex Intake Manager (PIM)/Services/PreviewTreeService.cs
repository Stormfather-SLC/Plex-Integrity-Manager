using PIM.Core.Models;
using PIM.Web.Models;

namespace PIM.Web.Services
{
    /// <summary>
    /// Builds a virtual preview tree from movie TargetPath values.
    /// This is used to show the user what the output folder/file structure
    /// will look like before any files are moved.
    ///
    /// The tree is intentionally state-aware:
    /// - valid/approved items are shown in the destination structure
    /// - Needs Review items are shown in a separate review section
    /// - Error items are shown in a separate error section
    ///
    /// Review/error items are visible in the preview, but they do not receive
    /// destination paths and are not eligible for commit.
    /// </summary>
    public class PreviewTreeService
    {
        public FileNode BuildTree(List<Movie> movies, string basePath)
        {
            var hasDestinationRoot = !string.IsNullOrWhiteSpace(basePath);
            var root = new FileNode
            {
                Name = hasDestinationRoot
                    ? basePath
                    : "Destination not configured",
                IsFolder = true,
                IsExpanded = true
            };

            // =====================================================
            // ✅ Destination Preview
            // =====================================================
            foreach (var movie in movies)
            {
                // Only include movies that are safe to keep/move in the
                // destination portion of the tree.
                var shouldKeep =
                    !movie.HasError &&
                    !movie.NeedsReview &&
                    (
                        movie.KeepRecommended ||
                        movie.IsAlternateVersion ||
                        !movie.IsDuplicate
                    );

                if (!shouldKeep || !hasDestinationRoot)
                    continue;

                if (string.IsNullOrWhiteSpace(movie.TargetPath))
                    continue;

                var relativePath = movie.TargetPath
                    .Replace(basePath, "")
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var parts = relativePath.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                AddToTree(root, parts, movie);
            }

            // =====================================================
            // 🔍 Needs Review Preview Section
            // =====================================================
            AddNeedsReviewSection(root, movies);

            // =====================================================
            // ❌ Error Preview Section
            // =====================================================
            AddErrorSection(root, movies);

            // IMPORTANT:
            // Count files AFTER the tree is built.
            // This prevents double-counting or missed counts.
            CalculateFileCounts(root);

            return root;
        }

        private void AddNeedsReviewSection(FileNode root, List<Movie> movies)
        {
            var reviewMovies = movies
                .Where(m => m.NeedsReview && !m.HasError)
                .OrderBy(m => Path.GetFileName(m.OriginalFilePath))
                .ToList();

            if (!reviewMovies.Any())
                return;

            var reviewRoot = new FileNode
            {
                Name = "Needs Review",
                IsFolder = true,
                IsExpanded = true,
                Status = "Needs Review"
            };

            foreach (var movie in reviewMovies)
            {
                reviewRoot.Children.Add(new FileNode
                {
                    Name = Path.GetFileName(movie.OriginalFilePath),
                    IsFolder = false,
                    Movie = movie,
                    Status = movie.ReviewReason ?? "Review required before commit"
                });
            }

            root.Children.Add(reviewRoot);
        }

        private void AddErrorSection(FileNode root, List<Movie> movies)
        {
            var errorMovies = movies
                .Where(m => m.HasError)
                .OrderBy(m => Path.GetFileName(m.OriginalFilePath))
                .ToList();

            if (!errorMovies.Any())
                return;

            var errorRoot = new FileNode
            {
                Name = "Errors",
                IsFolder = true,
                IsExpanded = true,
                Status = "Errors"
            };

            foreach (var movie in errorMovies)
            {
                errorRoot.Children.Add(new FileNode
                {
                    Name = Path.GetFileName(movie.OriginalFilePath),
                    IsFolder = false,
                    Movie = movie,
                    Status = movie.ErrorMessage ?? movie.ReviewReason ?? "Error must be resolved before commit"
                });
            }

            root.Children.Add(errorRoot);
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
                        Movie = isFile ? movie : null,
                        IsDuplicate = isFile && movie.IsDuplicate,
                        KeepRecommended = isFile && movie.KeepRecommended,
                        Status = isFile ? movie.Status : null
                    };

                    current.Children.Add(existing);
                }
                else if (isFile)
                {
                    // Ensures existing file nodes also get the movie reference.
                    existing.Movie = movie;
                    existing.Status = movie.Status;
                }

                current = existing;
            }
        }

        /// <summary>
        /// Recursively calculates how many files exist under each folder.
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
