using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IPlexLibraryConflictService
    {
        PlexLibraryConflictResult Check(Movie movie);
    }
}