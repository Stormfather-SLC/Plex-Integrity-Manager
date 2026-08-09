using System.Text.RegularExpressions;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services;

/// <summary>
/// Converts an ordered destination profile into a safe, deterministic path.
/// Every caller (preview, conflict detection, and live commit) should use the
/// resulting Movie.TargetPath rather than rebuilding organization rules.
/// </summary>
public sealed class DestinationPathBuilder : IDestinationPathBuilder
{
    private static readonly char[] ExplicitWindowsInvalidCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedWindowsNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        },
        StringComparer.OrdinalIgnoreCase);

    public DestinationPathResult Build(
        Movie movie,
        DestinationProfile profile,
        string sourceRoot,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(profile);

        profile.Normalize();

        if (string.IsNullOrWhiteSpace(profile.DestinationRoot))
        {
            throw new InvalidOperationException(
                $"Destination profile '{profile.Name}' does not have a destination root folder.");
        }

        var warnings = new List<string>();
        var organizationSegments = new List<string>();

        foreach (var level in profile.OrganizationLevels)
        {
            foreach (var segment in ResolveLevelSegments(
                         level,
                         movie,
                         sourceRoot,
                         warnings))
            {
                var safeSegment = SanitizePathSegment(segment);

                if (string.IsNullOrWhiteSpace(safeSegment))
                    continue;

                // Stacked rules can legitimately resolve to the same folder.
                // For example, MPA Rating may produce "R" while Preserve Source
                // Folders also begins with an existing "R" organization folder.
                // Do not create duplicate adjacent folders such as R\R.
                if (organizationSegments.Count > 0 &&
                    string.Equals(
                        organizationSegments[^1],
                        safeSegment,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                organizationSegments.Add(safeSegment);
            }
        }

        var movieFolderName = SanitizePathSegment(movie.GetNormalizedFolderName());
        var fileName = SanitizePathSegment(movie.GetNormalizedFileName(extension));

        if (string.IsNullOrWhiteSpace(movieFolderName) ||
            string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException(
                "The movie folder or file name became empty after path sanitization.");
        }

        var destinationRoot = Path.GetFullPath(profile.DestinationRoot);
        var pathParts = new List<string> { destinationRoot };
        pathParts.AddRange(organizationSegments);
        pathParts.Add(movieFolderName);

        var fullDirectoryPath = Path.GetFullPath(Path.Combine(pathParts.ToArray()));
        EnsurePathIsUnderRoot(destinationRoot, fullDirectoryPath);

        var fullFilePath = Path.GetFullPath(Path.Combine(fullDirectoryPath, fileName));
        EnsurePathIsUnderRoot(destinationRoot, fullFilePath);

        return new DestinationPathResult
        {
            DestinationRoot = destinationRoot,
            OrganizationSegments = organizationSegments,
            MovieFolderName = movieFolderName,
            FileName = fileName,
            FullDirectoryPath = fullDirectoryPath,
            FullFilePath = fullFilePath,
            Warnings = warnings
        };
    }

    private static IEnumerable<string> ResolveLevelSegments(
        DestinationOrganizationLevel level,
        Movie movie,
        string sourceRoot,
        List<string> warnings)
    {
        return level.Type switch
        {
            OrganizationLevelType.MpaRating =>
                [ResolveRating(movie.MpaRating, level.UnknownFolderName)],

            OrganizationLevelType.PrimaryGenre =>
                [ResolvePrimaryGenre(movie, level.UnknownFolderName)],

            OrganizationLevelType.LibraryCategory =>
                [ResolveLiteralValue(level, "Uncategorized", warnings)],

            OrganizationLevelType.FixedFolder =>
                [ResolveLiteralValue(level, "Folder", warnings)],

            OrganizationLevelType.AlphabeticalRange =>
                [ResolveAlphabeticalBucket(movie.Title, level)],

            OrganizationLevelType.PreserveSourceFolders =>
                ResolveSourceSegments(movie, sourceRoot, level, warnings),

            _ => Array.Empty<string>()
        };
    }

    private static string ResolveRating(string? rating, string? fallback)
    {
        var unknown = string.IsNullOrWhiteSpace(fallback) ? "Unrated" : fallback.Trim();

        if (string.IsNullOrWhiteSpace(rating))
            return unknown;

        var normalized = rating
            .Trim()
            .Replace('–', '-')
            .Replace('—', '-')
            .ToUpperInvariant();

        return normalized switch
        {
            "N/A" or "NA" or "NR" or "NOT RATED" or "UNRATED" or "UR" => unknown,
            _ => normalized
        };
    }

    private static string ResolvePrimaryGenre(Movie movie, string? fallback)
    {
        var genre = movie.PrimaryGenre;

        if (string.IsNullOrWhiteSpace(genre))
            genre = movie.Genres.FirstOrDefault();

        return string.IsNullOrWhiteSpace(genre)
            ? string.IsNullOrWhiteSpace(fallback) ? "Other" : fallback.Trim()
            : genre.Trim();
    }

    private static string ResolveLiteralValue(
        DestinationOrganizationLevel level,
        string fallback,
        List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(level.Value))
            return level.Value.Trim();

        var resolvedFallback = string.IsNullOrWhiteSpace(level.UnknownFolderName)
            ? fallback
            : level.UnknownFolderName.Trim();

        warnings.Add(
            $"{level.DisplayName} did not have a folder value. '{resolvedFallback}' was used.");

        return resolvedFallback;
    }

    private static string ResolveAlphabeticalBucket(
        string? title,
        DestinationOrganizationLevel level)
    {
        var buckets = level.AlphabeticalBuckets.Count == 0
            ? AlphabeticalBucket.CreateDefaultBuckets()
            : level.AlphabeticalBuckets;

        var sortTitle = GetSortTitle(title);
        var firstCharacter = sortTitle.FirstOrDefault(character => char.IsLetterOrDigit(character));

        if (!char.IsLetter(firstCharacter))
        {
            return buckets.FirstOrDefault(bucket => bucket.IncludeNumbersAndSymbols)?.FolderName
                   ?? level.UnknownFolderName
                   ?? "#'s";
        }

        var letter = char.ToUpperInvariant(firstCharacter);

        foreach (var bucket in buckets.Where(bucket => !bucket.IncludeNumbersAndSymbols))
        {
            if (!TryGetBoundary(bucket.StartLetter, out var start) ||
                !TryGetBoundary(bucket.EndLetter, out var end))
            {
                continue;
            }

            if (letter >= start && letter <= end)
                return bucket.FolderName;
        }

        return level.UnknownFolderName ?? "#'s";
    }

    private static IEnumerable<string> ResolveSourceSegments(
        Movie movie,
        string sourceRoot,
        DestinationOrganizationLevel level,
        List<string> warnings)
    {
        var sourceDirectory = movie.DirectoryPath;

        if (string.IsNullOrWhiteSpace(sourceDirectory))
            sourceDirectory = Path.GetDirectoryName(movie.OriginalFilePath);

        if (string.IsNullOrWhiteSpace(sourceRoot) ||
            string.IsNullOrWhiteSpace(sourceDirectory))
        {
            var fallback = level.UnknownFolderName ?? "Source";
            warnings.Add(
                $"The source-relative path could not be determined. '{fallback}' was used.");
            return [fallback];
        }

        string relativePath;

        try
        {
            relativePath = Path.GetRelativePath(
                Path.GetFullPath(sourceRoot),
                Path.GetFullPath(sourceDirectory));
        }
        catch
        {
            var fallback = level.UnknownFolderName ?? "Source";
            warnings.Add(
                $"The source-relative path was invalid. '{fallback}' was used.");
            return [fallback];
        }

        if (relativePath == ".")
            return Array.Empty<string>();

        if (relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            var fallback = level.UnknownFolderName ?? "Source";
            warnings.Add(
                $"The movie is outside the configured source root. '{fallback}' was used.");
            return [fallback];
        }

        var segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Preserve source organization folders, not the movie container itself.
        // The normalized movie folder is always appended by Build(), so keeping
        // an existing Plex movie folder here would produce Movie\Movie.
        if (segments.Count > 0 && IsExistingMovieFolder(movie, segments[^1]))
            segments.RemoveAt(segments.Count - 1);

        return segments;
    }

    private static bool IsExistingMovieFolder(Movie movie, string folderName)
    {
        var safeFolderName = SanitizePathSegment(folderName);
        var normalizedMovieFolder = SanitizePathSegment(movie.GetNormalizedFolderName());

        if (string.Equals(
                safeFolderName,
                normalizedMovieFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(movie.ImdbId))
            return false;

        var imdbTag = $"{{imdb-{movie.ImdbId.Trim()}}}";
        return safeFolderName.Contains(
            imdbTag,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSortTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var trimmed = title.Trim();
        var match = Regex.Match(
            trimmed,
            @"^(?<article>The|An|A)\s+(?<rest>.+)$",
            RegexOptions.IgnoreCase);

        return match.Success
            ? $"{match.Groups["rest"].Value}, {match.Groups["article"].Value}"
            : trimmed;
    }

    private static bool TryGetBoundary(string? value, out char boundary)
    {
        boundary = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        boundary = char.ToUpperInvariant(value.Trim()[0]);
        return char.IsLetter(boundary);
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var invalidCharacters = Path.GetInvalidFileNameChars()
            .Concat(ExplicitWindowsInvalidCharacters)
            .ToHashSet();

        var characters = value
            .Select(character =>
                character < 32 || invalidCharacters.Contains(character)
                    ? '_'
                    : character)
            .ToArray();

        var sanitized = Regex.Replace(new string(characters), @"\s+", " ")
            .Trim()
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(sanitized))
            return string.Empty;

        var reservedCandidate = Path.GetFileNameWithoutExtension(sanitized);

        if (ReservedWindowsNames.Contains(reservedCandidate))
            sanitized = $"_{sanitized}";

        return sanitized;
    }

    private static void EnsurePathIsUnderRoot(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(
                normalizedRoot,
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

        if (!normalizedCandidate.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The generated destination path escaped the configured destination root.");
        }
    }
}
