using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IRenameService
    {
        /// <summary>
        /// Generates the proposed target paths for each movie.
        /// </summary>
        void GeneratePreview(List<Movie> movies, string outputPath);

        /// <summary>
        /// Executes the file operations needed to move and rename
        /// the approved movies.
        ///
        /// If dryRun = true, no file system changes are made.
        /// </summary>
        void ExecuteChanges(List<Movie> movies, bool dryRun);
    }
}