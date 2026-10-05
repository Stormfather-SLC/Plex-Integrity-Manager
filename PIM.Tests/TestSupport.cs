namespace PIM.Tests;

/// <summary>
/// A newly created, isolated directory under the system temp folder. Dispose
/// deletes only that exact directory.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string prefix)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            prefix,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string SourceRoot => Path.Combine(Root, "Source");

    public string DestinationRoot => Path.Combine(Root, "Destination");

    public string StateDirectory => Path.Combine(Root, "State");

    public void Dispose()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Root);

        if (!root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(root))
        {
            return;
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Leave the isolated temp directory behind rather than fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow += amount;
}

/// <summary>
/// Wraps a real operation journal and runs an action once a given number of
/// files have been recorded as moved. Tests use it to press "Cancel" at an
/// exact point in a live commit: after one file has finished moving and
/// before the next one starts.
/// </summary>
internal sealed class ActAfterMovesJournal : PIM.Core.Interfaces.IOperationJournal
{
    private readonly PIM.Core.Interfaces.IOperationJournal _inner;
    private readonly int _afterMoves;
    private readonly Action _action;

    public ActAfterMovesJournal(
        PIM.Core.Interfaces.IOperationJournal inner,
        int afterMoves,
        Action action)
    {
        _inner = inner;
        _afterMoves = afterMoves;
        _action = action;
    }

    public PIM.Core.Interfaces.IOperationJournalRun StartRun(
        bool dryRun,
        string destinationRoot,
        int itemCount,
        int notApprovedCount = 0)
    {
        return new Run(
            _inner.StartRun(dryRun, destinationRoot, itemCount, notApprovedCount),
            _afterMoves,
            _action);
    }

    private sealed class Run : PIM.Core.Interfaces.IOperationJournalRun
    {
        private readonly PIM.Core.Interfaces.IOperationJournalRun _inner;
        private readonly int _afterMoves;
        private readonly Action _action;
        private int _moves;

        public Run(
            PIM.Core.Interfaces.IOperationJournalRun inner,
            int afterMoves,
            Action action)
        {
            _inner = inner;
            _afterMoves = afterMoves;
            _action = action;
        }

        public string Location => _inner.Location;

        public void Record(
            PIM.Core.Models.OperationJournalEntry entry,
            bool flushToDisk = true)
        {
            _inner.Record(entry, flushToDisk);

            if (entry.Event == PIM.Core.Models.OperationJournalEvent.Moved &&
                ++_moves == _afterMoves)
            {
                _action();
            }
        }

        public void Dispose() => _inner.Dispose();
    }
}
