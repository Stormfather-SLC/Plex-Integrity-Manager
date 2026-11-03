namespace PIM.Web.Pages;

/// <summary>
/// One change the owner lined up in Movie Results. The page keeps these
/// until the owner applies them, then posts them together so the plan is
/// rebuilt once. The server checks each again; the page only collects them.
/// </summary>
public sealed class QueuedDecision
{
    public const string AcceptMatch = "accept";
    public const string KeepFileName = "keepName";
    public const string UndoKeepFileName = "undoKeepName";
    public const string ImdbId = "imdb";
    public const string AddPlexDuplicate = "addPlexDuplicate";
    public const string UndoPlexDuplicate = "undoPlexDuplicate";
    public const string ConfirmFileName = "confirmName";
    public const string KeepDuplicateCopy = "keepCopy";

    public Guid MovieId { get; set; }

    public string? Kind { get; set; }

    /// <summary>The IMDb ID or page link typed for <see cref="ImdbId"/>.</summary>
    public string? Value { get; set; }

    public string Label => Kind switch
    {
        AcceptMatch => "Accept match",
        KeepFileName => "Keep file's name",
        UndoKeepFileName => "Undo keep name",
        ImdbId => "IMDb ID",
        AddPlexDuplicate => "Add anyway",
        UndoPlexDuplicate => "Undo add",
        ConfirmFileName => "Confirm name",
        KeepDuplicateCopy => "Keep this copy",
        _ => "Unknown change"
    };
}
