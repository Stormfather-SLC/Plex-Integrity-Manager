namespace PIM.Core.Models
{
    public enum DestinationConflictType
    {
        None,
        TargetFileAlreadyExists,
        TargetFolderAlreadyExists,
        SameImdbIdExistsInDestination,
        SimilarTitleYearExistsInDestination
    }

    public sealed class DestinationConflictResult
    {
        public bool HasConflict => ConflictType != DestinationConflictType.None;

        public DestinationConflictType ConflictType { get; init; }

        public string? Message { get; init; }

        public string? ExistingPath { get; init; }

        public static DestinationConflictResult NoConflict()
        {
            return new DestinationConflictResult
            {
                ConflictType = DestinationConflictType.None
            };
        }

        public static DestinationConflictResult Conflict(
            DestinationConflictType conflictType,
            string message,
            string? existingPath = null)
        {
            return new DestinationConflictResult
            {
                ConflictType = conflictType,
                Message = message,
                ExistingPath = existingPath
            };
        }
    }
}