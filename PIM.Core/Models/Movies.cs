using System;
using System.Collections.Generic;

namespace PIM.Core.Models
{
    public class Movie
    {
        // =========================================================
        // Identity
        // =========================================================

        public Guid Id { get; set; } = Guid.NewGuid();

        // =========================================================
        // Duplicate / Decision Logic
        // =========================================================

        public Guid? DuplicateGroupId { get; set; }

        public bool IsDuplicate { get; set; }

        public bool KeepRecommended { get; set; }

        public bool ApprovedForCommit { get; set; }

        public bool IsAlternateVersion { get; set; }

        // =========================================================
        // Review System
        // =========================================================

        public bool NeedsReview { get; set; }

        public string? ReviewReason { get; set; }

        /// <summary>
        /// Review reason currently owned by metadata identification. Keeping
        /// this separate lets retries replace transient metadata findings
        /// without clearing duplicate, edition, conflict, or human findings.
        /// </summary>
        public string? MetadataReviewReason { get; set; }

        public MetadataLookupFailureType MetadataLookupFailureType { get; set; }

        public string? MetadataLookupFailureDetail { get; set; }

        public bool HasMetadataReviewReason =>
            SplitReviewReasons(ReviewReason).Any(IsMetadataReviewReason);

        /// <summary>
        /// Marks the movie for human review without discarding an earlier reason.
        /// Automated pipeline stages may add review requirements, but only an
        /// explicit human-resolution workflow should clear them.
        /// </summary>
        public void RequireReview(string reason)
        {
            NeedsReview = true;
            ApprovedForCommit = false;

            if (string.IsNullOrWhiteSpace(reason))
                return;

            var existingReasons = (ReviewReason ?? string.Empty).Split(
                " | ",
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            var newReasons = reason.Split(
                " | ",
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var newReason in newReasons)
            {
                if (!existingReasons.Contains(
                        newReason,
                        StringComparer.OrdinalIgnoreCase))
                {
                    existingReasons.Add(newReason);
                }
            }

            ReviewReason = string.Join(" | ", existingReasons);
        }

        public const string DuplicateTieReviewPrefix = "No clear best file for ";

        /// <summary>
        /// True when duplicate analysis could not pick a preferred copy for
        /// this movie's edition and a human must choose one.
        /// </summary>
        public bool HasDuplicateTieReview =>
            SplitReviewReasons(ReviewReason).Any(IsDuplicateTieReason);

        /// <summary>
        /// Removes the duplicate-tie review reason after a human has chosen the
        /// preferred copy. Every other review reason is kept.
        /// </summary>
        public void ClearDuplicateTieReview() =>
            RemoveReviewReasons(IsDuplicateTieReason);

        private static bool IsDuplicateTieReason(string reason) =>
            reason.StartsWith(
                DuplicateTieReviewPrefix,
                StringComparison.OrdinalIgnoreCase);

        public const string SuspiciousFileNameReviewReason = "Suspicious file name";

        /// <summary>
        /// File names longer than this are sent to review because they often
        /// belong to samples, bundles, or mislabeled files.
        /// </summary>
        public const int SuspiciousFileNameLength = 120;

        public bool HasSuspiciousFileNameReview =>
            SplitReviewReasons(ReviewReason).Any(IsSuspiciousFileNameReason);

        /// <summary>
        /// Set when a human checked an unusually long file name and confirmed
        /// the file is the movie it appears to be. Every other check still applies.
        /// </summary>
        public bool FileNameConfirmed { get; set; }

        /// <summary>
        /// Removes only the suspicious-file-name review reason.
        /// </summary>
        public void ClearSuspiciousFileNameReview() =>
            RemoveReviewReasons(IsSuspiciousFileNameReason);

        private static bool IsSuspiciousFileNameReason(string reason) =>
            reason.Equals(
                SuspiciousFileNameReviewReason,
                StringComparison.OrdinalIgnoreCase);

        private void RemoveReviewReasons(Predicate<string> match)
        {
            var reasons = SplitReviewReasons(ReviewReason);

            if (reasons.RemoveAll(match) == 0)
                return;

            ReviewReason = reasons.Count == 0
                ? null
                : string.Join(" | ", reasons);
            NeedsReview = reasons.Count > 0;
        }

        public void SetMetadataReview(
            string reason,
            MetadataLookupFailureType failureType,
            string? failureDetail = null)
        {
            ClearMetadataReviewReasons();
            MetadataReviewReason = reason;
            MetadataLookupFailureType = failureType;
            MetadataLookupFailureDetail = failureDetail;
            RequireReview(reason);
        }

        public void ClearMetadataReviewReasons()
        {
            var reasons = SplitReviewReasons(ReviewReason);
            var removedMetadataReason = reasons.RemoveAll(IsMetadataReviewReason) > 0;

            ReviewReason = reasons.Count == 0
                ? null
                : string.Join(" | ", reasons);

            if (removedMetadataReason)
                NeedsReview = reasons.Count > 0;

            MetadataReviewReason = null;
            MetadataLookupFailureType = MetadataLookupFailureType.None;
            MetadataLookupFailureDetail = null;
        }

        private bool IsMetadataReviewReason(string reason)
        {
            if (!string.IsNullOrWhiteSpace(MetadataReviewReason) &&
                string.Equals(
                    reason,
                    MetadataReviewReason,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return reason.Equals(
                       "IMDb ID could not be determined",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.Equals(
                       "Missing IMDb ID",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.Equals(
                       "Missing required metadata for rename",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.Equals(
                       "IMDb ID found, but OMDb lookup failed",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith(
                       "IMDb ID matched, but OMDb did not return",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith(
                       "Low confidence metadata match",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith(
                       "Possible OMDb match:",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith(
                       "OMDb lookup failed:",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith(
                       "OMDb lookup not attempted:",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith(
                       "OMDb lookup succeeded but did not return",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> SplitReviewReasons(string? reasons)
        {
            return (reasons ?? string.Empty).Split(
                    " | ",
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .ToList();
        }

        // =========================================================
        // Conflict Detection
        // =========================================================

        public bool HasDestinationConflict { get; set; }

        public string? DestinationConflictReason { get; set; }

        public string? ExistingDestinationPath { get; set; }

        public bool HasPlexLibraryConflict { get; set; }

        public string? PlexLibraryConflictReason { get; set; }

        public string? ExistingPlexLibraryPath { get; set; }

        /// <summary>Resolution Plex reports for the existing copy, e.g. "1080p".</summary>
        public string? ExistingPlexResolution { get; set; }

        /// <summary>File size Plex reports for the existing copy.</summary>
        public long? ExistingPlexSizeBytes { get; set; }

        public bool IsPlexTrackedMigration { get; set; }

        /// <summary>
        /// Plex has what looks like the same movie as a different file. Set by
        /// conflict detection on every plan rebuild.
        /// </summary>
        public bool IsPossiblePlexDuplicate { get; set; }

        /// <summary>
        /// The existing Plex path the user chose to add this file alongside.
        /// The choice only applies while Plex still reports that same path.
        /// </summary>
        public string? PlexDuplicateAcceptedPath { get; set; }

        public bool HasAcceptedPlexDuplicate(string? existingPlexPath) =>
            !string.IsNullOrWhiteSpace(PlexDuplicateAcceptedPath) &&
            string.Equals(
                PlexDuplicateAcceptedPath,
                existingPlexPath,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>A possible duplicate awaiting the user's decision.</summary>
        public bool NeedsPlexDuplicateDecision =>
            IsPossiblePlexDuplicate && HasPlexLibraryConflict;

        /// <summary>A possible duplicate the user chose to add anyway.</summary>
        public bool IsPlexDuplicateAccepted =>
            IsPossiblePlexDuplicate &&
            !HasPlexLibraryConflict &&
            HasAcceptedPlexDuplicate(ExistingPlexLibraryPath);

        public string? PlexTrackedMigrationReason { get; set; }

        // =========================================================
        // Error System
        // =========================================================

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        // =========================================================
        // Version / Edition
        // =========================================================

        public string? VersionTag { get; set; }

        /// <summary>
        /// Set when a human chose this file as the preferred copy of a tied
        /// duplicate group. Duplicate analysis then keeps it instead of asking
        /// again; every other plan check still applies.
        /// </summary>
        public bool IsManuallyKept { get; set; }

        // =========================================================
        // Basic Identification and Organization Metadata
        // =========================================================

        public string? Title { get; set; }

        public int? Year { get; set; }

        public string? ImdbId { get; set; }

        /// <summary>
        /// OMDb Rated value used by the MPA Rating organization level.
        /// Values such as N/A, Not Rated, and NR are normalized by the
        /// destination path builder rather than stored differently here.
        /// </summary>
        public string? MpaRating { get; set; }

        /// <summary>
        /// Ordered genres returned by the metadata provider.
        /// </summary>
        public List<string> Genres { get; set; } = new();

        /// <summary>
        /// The first metadata genre. PIM uses only one genre folder so a movie
        /// is never copied into several genre branches.
        /// </summary>
        public string? PrimaryGenre { get; set; }

        // =========================================================
        // Matching
        // =========================================================

        public string? SuggestedTitle { get; set; }

        public int? SuggestedYear { get; set; }

        public string? SuggestedImdbId { get; set; }

        public bool HasMetadataSuggestion =>
            !string.IsNullOrWhiteSpace(SuggestedTitle) &&
            !string.IsNullOrWhiteSpace(SuggestedImdbId);

        public bool CanAcceptMetadataSuggestion =>
            HasMetadataSuggestion && SuggestedYear.HasValue;

        /// <summary>
        /// The IMDb ID for which the owner chose to keep the title and year
        /// from the file name instead of OMDb's wording for that ID. The ID is
        /// recorded, not just a flag, so the decision lapses by itself if the
        /// movie's IMDb ID ever changes.
        /// </summary>
        public string? FileIdentityKeptForImdbId { get; set; }

        public bool IsFileIdentityKept =>
            !string.IsNullOrWhiteSpace(FileIdentityKeptForImdbId) &&
            string.Equals(
                FileIdentityKeptForImdbId,
                ImdbId,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The name the movie has when the owner keeps the file's identity:
        /// the file's own title and year, with OMDb's used only for a part the
        /// file name does not provide.
        /// </summary>
        public string? FileIdentityTitle =>
            string.IsNullOrWhiteSpace(Title) ? SuggestedTitle : Title;

        public int? FileIdentityYear => Year ?? SuggestedYear;

        /// <summary>
        /// OMDb found this movie's IMDb ID but lists it under a different
        /// title or year than the file. The owner may then keep the file's
        /// wording, with the same IMDb ID, instead of accepting OMDb's.
        /// </summary>
        public bool CanKeepFileIdentity =>
            !HasError &&
            HasMetadataReviewReason &&
            MetadataLookupFailureType == MetadataLookupFailureType.ImdbIdentityConflict &&
            !string.IsNullOrWhiteSpace(ImdbId) &&
            string.Equals(ImdbId, SuggestedImdbId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(FileIdentityTitle) &&
            FileIdentityYear.HasValue;

        /// <summary>
        /// Manual IMDb ID entry is offered only while identity is unresolved.
        /// </summary>
        public bool CanEnterImdbId =>
            !HasError &&
            (HasMetadataReviewReason || string.IsNullOrWhiteSpace(ImdbId));

        public double? MatchConfidence { get; set; }

        public bool IsFuzzyMatch { get; set; }

        public bool MetadataMatchedByImdbId { get; set; }

        public MetadataMatchOrigin MetadataMatchOrigin { get; set; }

        public string? MetadataDiscoveryReason { get; set; }

        // =========================================================
        // File Information
        // =========================================================

        public string OriginalFilePath { get; set; } = string.Empty;

        public string? DirectoryPath { get; set; }

        public string? FileName { get; set; }

        public long FileSizeBytes { get; set; }

        /// <summary>
        /// Resolution read from the file name ("1080p", "4K"), or null. A hint
        /// for comparison with an existing Plex copy; the video is not inspected.
        /// </summary>
        public string? SourceResolution { get; set; }

        public string? OriginalPath => OriginalFilePath;

        // =========================================================
        // Processing State
        // =========================================================

        public bool MetadataFetched { get; set; }

        public List<int> CandidateYears { get; set; } = new();

        // =========================================================
        // Output / Rename
        // =========================================================

        public string? NormalizedFolderName { get; set; }

        public string? NormalizedFileName { get; set; }

        public string? TargetPath { get; set; }

        /// <summary>
        /// Identifies the destination profile that generated TargetPath.
        /// A changed profile revision invalidates the previous plan.
        /// </summary>
        public Guid? DestinationProfileId { get; set; }

        public int DestinationProfileRevision { get; set; }

        /// <summary>
        /// Workflow policy used for the current target/conflict plan. Metadata
        /// remains reusable when this changes; only downstream planning reruns.
        /// </summary>
        public LibraryGoal? PlannedLibraryGoal { get; set; }

        // =========================================================
        // Status / Errors
        // =========================================================

        public string Status { get; set; } = "Pending";

        public string? ErrorMessage { get; set; }

        // =========================================================
        // UI Helper
        // =========================================================

        public string GetColor()
        {
            if (HasError)
                return "red";

            if (NeedsReview)
                return "blue";

            if (IsDuplicate && !KeepRecommended && !IsAlternateVersion)
                return "orange";

            if (KeepRecommended)
                return "yellow";

            if (IsAlternateVersion)
                return "green";

            return "neutral";
        }

        // =========================================================
        // Naming Helpers
        // =========================================================

        private static bool ShouldIncludeEditionTag(string? versionTag)
        {
            if (string.IsNullOrWhiteSpace(versionTag))
                return false;

            var normalized = versionTag.Trim();

            return !normalized.Equals(
                "Alternate Version",
                StringComparison.OrdinalIgnoreCase);
        }

        public string GetNormalizedFolderName()
        {
            ValidateRequiredMetadata();
            return $"{Title} ({Year}) {{imdb-{ImdbId}}}";
        }

        public string GetNormalizedFileName(string extension)
        {
            ValidateRequiredMetadata();

            var editionPart = ShouldIncludeEditionTag(VersionTag)
                ? $" {{edition-{VersionTag!.Trim()}}}"
                : string.Empty;

            return $"{Title} ({Year}){editionPart} {{imdb-{ImdbId}}}{extension}";
        }

        private void ValidateRequiredMetadata()
        {
            if (string.IsNullOrWhiteSpace(Title) ||
                Year == null ||
                string.IsNullOrWhiteSpace(ImdbId))
            {
                throw new InvalidOperationException(
                    "Movie is missing required metadata.");
            }
        }
    }
}
