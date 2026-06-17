using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    /// <summary>
    /// Placeholder for Plex-aware conflict detection.
    ///
    /// Future implementation should query Plex or inspect a cached Plex library
    /// index to determine whether Plex already knows about the same movie at a
    /// different location.
    /// </summary>
    public class PlexLibraryConflictService : IPlexLibraryConflictService
    {
        public PlexLibraryConflictResult Check(Movie movie)
        {
            return PlexLibraryConflictResult.NoConflict();
        }
    }
}