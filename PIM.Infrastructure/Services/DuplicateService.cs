using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    /// <summary>
    /// Analyzes a collection of movies and determines which files should be:
    ///
    /// - Kept as the preferred version
    /// - Kept as valid alternate versions
    /// - Excluded as unnecessary duplicates
    /// - Flagged for manual review
    ///
    /// DECISION OUTCOMES
    /// -----------------
    /// 🟡 KeepRecommended     = Primary version to keep
    /// 🟢 IsAlternateVersion  = Valid alternate cut (Extended, Director's Cut, etc.)
    /// 🟠 IsDuplicate         = Duplicate that is not recommended for commit
    /// 🔵 NeedsReview         = Requires human decision
    ///
    /// APPROVAL LOGIC
    /// --------------
    /// ApprovedForCommit is automatically set to true for:
    /// - Clean, unique movies
    /// - Recommended duplicate winners
    /// - Approved alternate versions
    ///
    /// It remains false for:
    /// - Duplicate copies to discard
    /// - Movies requiring review
    /// - Movies with errors
    /// </summary>
    public class DuplicateService : IDuplicateService
    {
        /// <summary>
        /// Minimum size difference required to confidently determine
        /// that one duplicate is superior to another.
        /// </summary>
        private const long SizeToleranceBytes = 50 * 1024 * 1024; // 50 MB

        private const string NormalVersionKey = "__NORMAL__";
        private const string NormalVersionLabel = "Standard Version";

        /// <summary>
        /// Processes all movies and assigns duplicate, review,
        /// and commit approval flags.
        /// </summary>
        /// <param name="movies">Movies to analyze.</param>
        public void Process(List<Movie> movies)
        {
            ResetDecisionFlags(movies);

            ProcessDuplicateGroups(movies);

            ApplyGlobalReviewRules(movies);

            AutoApproveCleanUniqueMovies(movies);
        }

        // =====================================================
        // 🔄 RESET
        // =====================================================

        /// <summary>
        /// Clears all decision-related flags so processing can be
        /// safely re-run multiple times.
        /// </summary>
        private void ResetDecisionFlags(List<Movie> movies)
        {
            foreach (var movie in movies)
            {
                movie.IsDuplicate = false;
                movie.DuplicateGroupId = null;
                movie.KeepRecommended = false;
                movie.ApprovedForCommit = false;

                // Preserve edition/version information detected by FileNameParser.
                // Examples: Edited, Family Edit, Clean Version, Director's Cut, IMAX.
                // RenameService needs this value later to generate:
                // {edition-VersionTag}
                //
                // Do NOT clear:
                // movie.VersionTag

                // IsAlternateVersion is a decision flag, so it is recalculated below.
                movie.IsAlternateVersion = false;

                movie.NeedsReview = false;
                movie.ReviewReason = null;
            }
        }

        // =====================================================
        // 🎬 DUPLICATE PROCESSING
        // =====================================================

        /// <summary>
        /// Groups movies by IMDb ID and determines duplicate outcomes.
        /// </summary>
        private void ProcessDuplicateGroups(List<Movie> movies)
        {
            var groups = movies
                .Where(m => !string.IsNullOrWhiteSpace(m.ImdbId))
                .GroupBy(m => m.ImdbId!.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                // Single movie = no duplicates.
                if (group.Count() <= 1)
                    continue;

                ProcessDuplicateGroup(group.ToList());
            }
        }

        /// <summary>
        /// Processes a single duplicate group.
        ///
        /// Important:
        /// Duplicates are compared by IMDb ID + edition/version tag.
        /// This means a normal cut and an Extended Edition can both be kept,
        /// but duplicate copies inside the same edition are reduced to the
        /// best/largest file.
        /// </summary>
        private void ProcessDuplicateGroup(List<Movie> group)
        {
            var groupId = Guid.NewGuid();

            // First mark every movie in this IMDb group as part of a duplicate group.
            foreach (var movie in group)
            {
                movie.IsDuplicate = true;
                movie.DuplicateGroupId = groupId;

                // Preserve VersionTag, but reset duplicate decision flags.
                movie.IsAlternateVersion = false;
                movie.KeepRecommended = false;
                movie.ApprovedForCommit = false;
            }

            var versionGroups = group
                .GroupBy(GetVersionKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key == NormalVersionKey ? 0 : 1)
                .ThenBy(g => g.Key);

            foreach (var versionGroup in versionGroups)
            {
                ProcessVersionGroup(versionGroup.Key, versionGroup.ToList());
            }
        }

        /// <summary>
        /// Keeps one best file inside a single edition/version group.
        /// </summary>
        private void ProcessVersionGroup(string versionKey, List<Movie> versionGroup)
        {
            var ordered = versionGroup
                .OrderByDescending(m => m.FileSizeBytes)
                .ThenBy(m => m.FileName)
                .ToList();

            var bestForThisVersion = ordered.First();

            bool hasClearWinner =
                ordered.Count == 1 ||
                ordered[0].FileSizeBytes - ordered[1].FileSizeBytes > SizeToleranceBytes;

            if (!hasClearWinner)
            {
                var versionLabel = GetVersionLabel(versionKey);

                foreach (var movie in ordered)
                {
                    movie.NeedsReview = true;
                    movie.ReviewReason = $"No clear best file for {versionLabel}";
                    movie.ApprovedForCommit = false;
                    movie.KeepRecommended = false;
                    movie.IsAlternateVersion = false;
                }

                return;
            }

            if (versionKey == NormalVersionKey)
            {
                // Primary / standard version winner.
                bestForThisVersion.KeepRecommended = true;
                bestForThisVersion.IsAlternateVersion = false;
            }
            else
            {
                // Edition winner, such as Edited, Extended Edition,
                // Theatrical Cut, IMAX, etc.
                bestForThisVersion.KeepRecommended = false;
                bestForThisVersion.IsAlternateVersion = true;
            }

            bestForThisVersion.ApprovedForCommit = true;

            foreach (var duplicate in ordered.Skip(1))
            {
                duplicate.KeepRecommended = false;
                duplicate.IsAlternateVersion = false;
                duplicate.ApprovedForCommit = false;

                // Keep duplicate.VersionTag as-is for display and audit visibility.
            }
        }

        // =====================================================
        // 🌐 GLOBAL REVIEW RULES
        // =====================================================

        /// <summary>
        /// Applies review rules to all movies.
        /// </summary>
        private void ApplyGlobalReviewRules(List<Movie> movies)
        {
            foreach (var movie in movies)
            {
                // Missing IMDb ID
                if (string.IsNullOrWhiteSpace(movie.ImdbId))
                {
                    movie.NeedsReview = true;
                    movie.ReviewReason ??= "Missing IMDb ID";
                }

                // Suspiciously long filename
                if (!string.IsNullOrEmpty(movie.FileName) &&
                    movie.FileName.Length > 120)
                {
                    movie.NeedsReview = true;
                    movie.ReviewReason ??= "Suspicious file name";
                }

                // Low-confidence fuzzy match
                if (movie.IsFuzzyMatch &&
                    movie.MatchConfidence < 0.7)
                {
                    movie.NeedsReview = true;
                    movie.ReviewReason ??= "Low confidence match";
                }

                // Anything requiring review should not be auto-approved.
                if (movie.NeedsReview)
                {
                    movie.ApprovedForCommit = false;
                    movie.KeepRecommended = false;
                    movie.IsAlternateVersion = false;
                }
            }
        }

        // =====================================================
        // ✅ AUTO APPROVAL
        // =====================================================

        /// <summary>
        /// Automatically approves unique movies that have no
        /// errors and do not require review.
        /// </summary>
        private void AutoApproveCleanUniqueMovies(List<Movie> movies)
        {
            foreach (var movie in movies)
            {
                if (!movie.IsDuplicate &&
                    !movie.NeedsReview &&
                    !movie.HasError)
                {
                    movie.ApprovedForCommit = true;
                }
            }
        }

        // =====================================================
        // 🔍 HELPER METHODS
        // =====================================================

        /// <summary>
        /// Returns the grouping key used for edition-aware duplicate handling.
        /// </summary>
        private static string GetVersionKey(Movie movie)
        {
            if (string.IsNullOrWhiteSpace(movie.VersionTag))
                return NormalVersionKey;

            return NormalizeVersionTag(movie.VersionTag);
        }

        /// <summary>
        /// Normalizes edition names so minor spacing/casing differences
        /// do not create separate duplicate groups.
        /// </summary>
        private static string NormalizeVersionTag(string versionTag)
        {
            return versionTag
                .Trim()
                .Replace("’", "'")
                .Replace("  ", " ")
                .ToUpperInvariant();
        }

        private static string GetVersionLabel(string versionKey)
        {
            return versionKey == NormalVersionKey
                ? NormalVersionLabel
                : versionKey;
        }
    }
}
