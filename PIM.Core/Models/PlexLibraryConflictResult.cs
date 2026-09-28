namespace PIM.Core.Models
{
    public enum PlexLibraryConflictType
    {
        None,
        AlreadyExistsAtTargetPath,
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

        /// <summary>Plex's reported resolution for the existing copy, if known.</summary>
        public string? ExistingResolution { get; init; }

        /// <summary>Plex's reported file size for the existing copy, if known.</summary>
        public long? ExistingSizeBytes { get; init; }

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
            string? existingPath = null,
            string? existingResolution = null,
            long? existingSizeBytes = null)
        {
            return new PlexLibraryConflictResult
            {
                ConflictType = conflictType,
                Message = message,
                ExistingPath = existingPath,
                ExistingResolution = existingResolution,
                ExistingSizeBytes = existingSizeBytes
            };
        }
    }
}