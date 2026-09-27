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
