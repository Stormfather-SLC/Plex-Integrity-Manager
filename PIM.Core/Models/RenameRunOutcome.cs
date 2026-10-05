namespace PIM.Core.Models;

/// <summary>
/// What a dry run or live commit actually did with the approved files.
/// </summary>
/// <param name="MovedCount">
/// Files moved by this run. Always zero for a dry run.
/// </param>
/// <param name="NotReachedCount">
/// Approved files the run never started on because the owner stopped it. They
/// were left exactly where they are.
/// </param>
public sealed record RenameRunOutcome(int MovedCount, int NotReachedCount)
{
    public static readonly RenameRunOutcome Nothing = new(0, 0);

    /// <summary>
    /// The owner stopped the run before every approved file had been handled.
    /// </summary>
    public bool StoppedByUser => NotReachedCount > 0;
}
