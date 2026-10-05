namespace PIM.Core.Models;

/// <summary>Kinds of events written to the operation journal.</summary>
public enum OperationJournalEvent
{
    RunStarted,
    WouldMove,
    MoveStarting,
    Moved,
    Skipped,
    Conflict,
    Failed,
    SourceFolderRemoved,
    RunCompleted,
    RunAborted,

    /// <summary>
    /// The owner stopped the run before every approved file was handled.
    /// </summary>
    RunStopped
}

/// <summary>One line in the operation journal.</summary>
public sealed record OperationJournalEntry(
    OperationJournalEvent Event,
    string? SourcePath = null,
    string? TargetPath = null,
    string? Title = null,
    int? Year = null,
    string? ImdbId = null,
    string? Status = null,
    string? Detail = null)
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public static OperationJournalEntry ForMovie(
        OperationJournalEvent journalEvent,
        Movie movie,
        string? detail = null) =>
        new(
            journalEvent,
            movie.OriginalFilePath,
            movie.TargetPath,
            movie.Title,
            movie.Year,
            movie.ImdbId,
            movie.Status,
            detail);
}
