using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IRenameService
    {
        /// <summary>
        /// Operation journal file written by the most recent dry run or live
        /// commit, or null when none was written.
        /// </summary>
        string? LastJournalLocation { get; }

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
        /// <paramref name="notApproved"/> lists the scanned movies that are not
        /// part of this run; they are only recorded in the operation journal as
        /// skipped, with the reason, and are never touched or modified.
        /// </summary>
        void ExecuteChanges(
            List<Movie> movies,
            bool dryRun,
            string destinationRoot,
            IReadOnlyCollection<Movie>? notApproved = null);
    }
}
