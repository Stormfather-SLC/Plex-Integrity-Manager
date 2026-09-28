using PIM.Core.Models;

namespace PIM.Infrastructure.Services;

public enum PlexPathRelationship
{
    Indeterminate,
    InsideSource,
    OutsideSource
}

/// <param name="IsTrackedMigration">Proceed: this is the file Plex tracks.</param>
/// <param name="IsDuplicateDecision">
/// A possible duplicate the user decides on (skip by default, or add anyway).
/// When both flags are false the Plex result is a hard stop.
/// </param>
public sealed record PlexMatchPolicyDecision(
    bool IsTrackedMigration,
    string Message,
    PlexPathRelationship PathRelationship,
    bool IsDuplicateDecision = false);

public static class PlexMatchPolicy
{
    public static PlexMatchPolicyDecision Evaluate(
        LibraryGoal libraryGoal,
        string? sourceRoot,
        Movie movie,
        PlexLibraryConflictResult plexResult)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(plexResult);

        var originalMessage = plexResult.Message ??
                              "A Plex library conflict was detected.";
        var relationship = GetPathRelationship(
            sourceRoot,
            plexResult.ExistingPath);

        // Moving the very file Plex tracks is what Reorganize / Migrate is for.
        // In the other workflows it is not a new movie, so it stays blocked.
        if (plexResult.ConflictType == PlexLibraryConflictType.TracksThisFile)
        {
            return libraryGoal == LibraryGoal.ReorganizationMigration &&
                   !string.IsNullOrWhiteSpace(movie.ImdbId)
                ? new PlexMatchPolicyDecision(
                    true,
                    $"Plex tracks this exact file (IMDb {movie.ImdbId}). Moving it reorganizes the file Plex already knows; Plex finds it at the new location on its next library scan.",
                    relationship)
                : new PlexMatchPolicyDecision(
                    false,
                    $"{originalMessage} Use the Reorganize / Migrate workflow to move a file Plex already tracks.",
                    relationship);
        }

        // Same movie as a different file: it may be a better or different
        // version, so the user decides. Consolidation exists to avoid adding
        // copies, so it keeps these blocked.
        if (plexResult.IsPossibleDuplicate &&
            libraryGoal != LibraryGoal.Consolidation)
        {
            return new PlexMatchPolicyDecision(
                false,
                $"Possible duplicate. {originalMessage}",
                relationship,
                IsDuplicateDecision: true);
        }

        // Target-path collisions, identity mismatches, and failed Plex checks
        // are hard stops in every workflow.
        return new PlexMatchPolicyDecision(
            false,
            originalMessage,
            relationship);
    }

    public static PlexPathRelationship GetPathRelationship(
        string? sourceRoot,
        string? plexPath)
    {
        if (!TryNormalizeFullyQualifiedPath(sourceRoot, out var source) ||
            !TryNormalizeFullyQualifiedPath(plexPath, out var candidate))
        {
            return PlexPathRelationship.Indeterminate;
        }

        if (string.Equals(
                source,
                candidate,
                StringComparison.OrdinalIgnoreCase))
        {
            return PlexPathRelationship.InsideSource;
        }

        var sourceWithSeparator = source.EndsWith(
            Path.DirectorySeparatorChar)
            ? source
            : source + Path.DirectorySeparatorChar;

        return candidate.StartsWith(
            sourceWithSeparator,
            StringComparison.OrdinalIgnoreCase)
            ? PlexPathRelationship.InsideSource
            : PlexPathRelationship.OutsideSource;
    }

    private static bool TryNormalizeFullyQualifiedPath(
        string? path,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var platformPath = path.Trim()
                .Replace(
                    Path.AltDirectorySeparatorChar,
                    Path.DirectorySeparatorChar);

            if (!Path.IsPathFullyQualified(platformPath))
                return false;

            var fullPath = Path.GetFullPath(platformPath);
            var pathRoot = Path.GetPathRoot(fullPath);

            if (string.IsNullOrWhiteSpace(pathRoot))
                return false;

            normalizedPath = string.Equals(
                fullPath,
                pathRoot,
                StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
