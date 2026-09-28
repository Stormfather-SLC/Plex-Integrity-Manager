namespace PIM.Core.Models
{
    public enum PlexLibraryConflictType
    {
        None,
        AlreadyExistsAtTargetPath,
        SameImdbIdDifferentPath,
        SameTitleYearDifferentPath,
        ExistingAlternateVersion,
        LibraryMismatch,

        /// <summary>
        /// Plex already tracks this exact source file (same path and identity).
        /// Moving it reorganizes the file Plex knows rather than adding a copy.
        /// </summary>
        TracksThisFile
    }

    public sealed class PlexLibraryConflictResult
    {
        /// <summary>
        /// Plex has what looks like the same movie as a different file: the
        /// same IMDb ID or title/year elsewhere, or another edition. The new
        /// file may still be a different version (resolution, encode, cut), so
        /// this is a decision for the user rather than a hard stop.
        /// </summary>
        public bool IsPossibleDuplicate => ConflictType is
            PlexLibraryConflictType.SameImdbIdDifferentPath or
            PlexLibraryConflictType.SameTitleYearDifferentPath or
            PlexLibraryConflictType.ExistingAlternateVersion;

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