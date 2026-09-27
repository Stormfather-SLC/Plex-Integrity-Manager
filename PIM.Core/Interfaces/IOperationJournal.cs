using PIM.Core.Models;

namespace PIM.Core.Interfaces;

/// <summary>
/// Durable, append-only record of every dry run and live commit: what PIM
/// planned, what it moved, what it skipped and why, and which source folders
/// it removed. A live commit must not move a file it could not record.
/// </summary>
public interface IOperationJournal
{
    /// <summary>
    /// Starts a new run and returns a handle used to record its events.
    /// Throws when the journal cannot be created.
    /// </summary>
    IOperationJournalRun StartRun(
        bool dryRun,
        string destinationRoot,
        int itemCount);
}

public interface IOperationJournalRun : IDisposable
{
    /// <summary>Location of the run's journal, for display to the user.</summary>
    string Location { get; }

    /// <summary>
    /// Appends and flushes one entry to durable storage. Throws when the entry
    /// could not be written.
    /// </summary>
    void Record(OperationJournalEntry entry);
}

/// <summary>Used where no journal is configured (for example, unit tests).</summary>
public sealed class NullOperationJournal : IOperationJournal
{
    public static readonly NullOperationJournal Instance = new();

    public IOperationJournalRun StartRun(
        bool dryRun,
        string destinationRoot,
        int itemCount) => NullRun.Instance;

    private sealed class NullRun : IOperationJournalRun
    {
        public static readonly NullRun Instance = new();

        public string Location => string.Empty;

        public void Record(OperationJournalEntry entry)
        {
        }

        public void Dispose()
        {
        }
    }
}
