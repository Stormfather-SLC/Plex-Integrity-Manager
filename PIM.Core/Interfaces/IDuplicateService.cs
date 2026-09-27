using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IDuplicateService
    {
        void Process(List<Movie> movies);

        /// <summary>
        /// Records a human choice of <paramref name="chosen"/> as the preferred
        /// copy of its tied duplicate group (same IMDb ID and edition) and clears
        /// the tie review reason for that group. Returns false, changing
        /// nothing, when <paramref name="chosen"/> is not part of such a tie.
        /// The caller must rebuild the plan afterwards.
        /// </summary>
        bool ChoosePreferredCopy(Movie chosen, List<Movie> movies);
    }
}