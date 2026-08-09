using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IRenameService
    {
        /// <summary>
        /// Generates proposed target paths using the selected destination
        /// profile and its ordered organization levels.
        /// </summary>
        void GeneratePreview(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot);

        /// <summary>
        /// Executes approved file operations. The destination root is passed
        /// explicitly so the final conflict scan covers the entire library,
        /// even when the profile creates several nested organization folders.
        /// </summary>
        void ExecuteChanges(
            List<Movie> movies,
            bool dryRun,
            string destinationRoot);
    }
}
