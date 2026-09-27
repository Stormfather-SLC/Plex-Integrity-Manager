using PIM.Core.Models;

namespace PIM.Core.Interfaces;

/// <summary>
/// Holds the current scan (including identification results and human review
/// decisions) and the latest dry-run preview/approval, and persists them so
/// they survive an application restart.
/// </summary>
/// <remarks>
/// Restored state is never trusted on its own. A scan recorded for a different
/// source folder is discarded, an unreadable file is set aside rather than
/// partially loaded, and a dry-run approval still expires after
/// <see cref="DryRunApproval.MaxAge"/>. Live commit continues to rebuild the
/// plan and compare its fingerprint against the approval before any move.
/// </remarks>
public interface IWorkflowStateStore
{
    /// <summary>
    /// Returns the current scan. The same list instance is returned on every
    /// call; call <see cref="SaveMovies"/> after changing it.
    /// </summary>
    bool TryGetMovies(out List<Movie> movies);

    /// <summary>
    /// Makes <paramref name="movies"/> the current scan for
    /// <paramref name="sourceRoot"/> and writes it to durable storage.
    /// </summary>
    void SaveMovies(List<Movie> movies, string sourceRoot);

    /// <summary>Discards the scan and any dry-run approval.</summary>
    void ClearMovies();

    /// <summary>Preview from the most recent unexpired dry run, if any.</summary>
    DryRunPreviewResult? GetDryRunPreview();

    /// <summary>The most recent unexpired dry-run approval, if any.</summary>
    DryRunApproval? GetDryRunApproval();

    /// <summary>Records a completed dry run and its approval.</summary>
    void SaveDryRun(DryRunPreviewResult preview, DryRunApproval approval);

    /// <summary>
    /// Removes the dry-run preview and approval from memory and durable storage.
    /// </summary>
    void InvalidateDryRunApproval();
}
