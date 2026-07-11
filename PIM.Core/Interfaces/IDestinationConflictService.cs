using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IDestinationConflictService
    {
        /// <summary>
        /// Clears the cached destination snapshot so the next check reads
        /// the current file-system state.
        /// </summary>
        void InvalidateCache();

        /// <summary>
        /// Adds a newly committed file to the current destination snapshot.
        /// This avoids rescanning the entire destination library after every
        /// successful move while keeping later checks aware of the new file.
        /// </summary>
        void RecordDestinationEntry(string path);

        /// <summary>
        /// Checks a proposed movie target for conflicts in the destination.
        /// Set refresh to true only when a completely fresh destination snapshot
        /// is required. Normal batch commits should reuse the snapshot generated
        /// immediately before commit.
        /// </summary>
        DestinationConflictResult Check(
            Movie movie,
            string outputPath,
            bool refresh = false);
    }
}
