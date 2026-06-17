namespace PIM.Core.Models
{
    public enum PlexLibraryConflictType
    {
        None,
        SameImdbIdDifferentPath,
        SameTitleYearDifferentPath,
        ExistingAlternateVersion,
        LibraryMismatch
    }

    public sealed class PlexLibraryConflictResult
    {
        public bool HasConflict => ConflictType != PlexLibraryConflictType.None;

        public PlexLibraryConflictType ConflictType { get; init; }

        public string? Message { get; init; }

        public string? ExistingPath { get; init; }

        public static PlexLibraryConflictResult NoConflict()
        {
            return new PlexLibraryConflictResult
            {
                ConflictType = PlexLibraryConflictType.None
            };
        }

        public static PlexLibraryConflictResult Conflict(
            PlexLibraryConflictType conflictType,
            string message,
            string? existingPath = null)
        {
            return new PlexLibraryConflictResult
            {
                ConflictType = conflictType,
                Message = message,
                ExistingPath = existingPath
            };
        }
    }
}