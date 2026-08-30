using PIM.Core.Models;

namespace PIM.Infrastructure.Services;

public enum PlexPathRelationship
{
    Indeterminate,
    InsideSource,
    OutsideSource
}

public sealed record PlexMatchPolicyDecision(
    bool IsTrackedMigration,
    string Message,
    PlexPathRelationship PathRelationship);

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

        if (libraryGoal != LibraryGoal.ReorganizationMigration ||
            !IsVerifiedImdbMatch(movie, plexResult))
        {
            return new PlexMatchPolicyDecision(
                false,
                originalMessage,
                PlexPathRelationship.Indeterminate);
        }

        var relationship = GetPathRelationship(
            sourceRoot,
            plexResult.ExistingPath);

        var location = relationship switch
        {
            PlexPathRelationship.InsideSource => "inside the selected source tree",
            PlexPathRelationship.OutsideSource => "outside the selected source tree",
            _ => "at a path whose relationship to the source tree could not be determined"
        };

        // In the migration workflow, a verified same-IMDb Plex entry is useful
        // read-only awareness, not a conflict by itself. Destination collisions,
        // ambiguous identity, duplicates, and every other safety check still apply.
        return new PlexMatchPolicyDecision(
            true,
            $"Plex awareness: Plex contains IMDb {movie.ImdbId} {location}. This does not block the selected migration workflow by itself.",
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

    private static bool IsVerifiedImdbMatch(
        Movie movie,
        PlexLibraryConflictResult plexResult)
    {
        if (string.IsNullOrWhiteSpace(movie.ImdbId))
            return false;

        return plexResult.ConflictType is
            PlexLibraryConflictType.AlreadyExistsAtTargetPath or
            PlexLibraryConflictType.SameImdbIdDifferentPath or
            PlexLibraryConflictType.ExistingAlternateVersion;
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
