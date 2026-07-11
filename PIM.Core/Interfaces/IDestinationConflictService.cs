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
        /// Checks a proposed movie target for conflicts in the destination.
        /// Set refresh to true for a fresh file-system scan immediately before
        /// a live move.
        /// </summary>
        DestinationConflictResult Check(
            Movie movie,
            string outputPath,
            bool refresh = false);
    }
}
