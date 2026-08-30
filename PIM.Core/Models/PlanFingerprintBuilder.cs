using System.Security.Cryptography;
using System.Text;

namespace PIM.Core.Models;

/// <summary>
/// Creates the immutable approval fingerprint shared by dry run and live commit.
/// Every field here can affect identity, action, destination, or executability.
/// </summary>
public static class PlanFingerprintBuilder
{
    public static string Build(
        IEnumerable<Movie> movies,
        DestinationProfile profile,
        LibraryGoal libraryGoal)
    {
        ArgumentNullException.ThrowIfNull(movies);
        ArgumentNullException.ThrowIfNull(profile);

        var planLines = movies
            .OrderBy(movie => movie.Id)
            .Select(movie => string.Join(
                '|',
                movie.Id.ToString("N"),
                movie.OriginalFilePath,
                movie.Title ?? string.Empty,
                movie.Year?.ToString() ?? string.Empty,
                movie.ImdbId ?? string.Empty,
                movie.VersionTag ?? string.Empty,
                movie.MetadataFetched,
                movie.MatchConfidence?.ToString("R") ?? string.Empty,
                movie.TargetPath ?? string.Empty,
                movie.ApprovedForCommit,
                movie.NeedsReview,
                movie.ReviewReason ?? string.Empty,
                movie.HasError,
                movie.ErrorMessage ?? string.Empty,
                movie.IsDuplicate,
                movie.KeepRecommended,
                movie.HasDestinationConflict,
                movie.DestinationConflictReason ?? string.Empty,
                movie.ExistingDestinationPath ?? string.Empty,
                movie.HasPlexLibraryConflict,
                movie.PlexLibraryConflictReason ?? string.Empty,
                movie.ExistingPlexLibraryPath ?? string.Empty,
                movie.IsPlexTrackedMigration,
                movie.PlannedLibraryGoal?.ToString() ?? string.Empty));

        var payload = string.Join(
            Environment.NewLine,
            new[]
            {
                profile.Id.ToString("N"),
                profile.Revision.ToString(),
                profile.DestinationRoot,
                libraryGoal.ToString()
            }.Concat(planLines));

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
