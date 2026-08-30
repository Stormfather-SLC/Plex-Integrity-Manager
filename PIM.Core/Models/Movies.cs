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

        public bool IsPlexTrackedMigration { get; set; }

        public string? PlexTrackedMigrationReason { get; set; }

        // =========================================================
        // Error System
        // =========================================================

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        // =========================================================
        // Version / Edition
        // =========================================================

        public string? VersionTag { get; set; }

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

        public double? MatchConfidence { get; set; }

        public bool IsFuzzyMatch { get; set; }

        public bool MetadataMatchedByImdbId { get; set; }

        // =========================================================
        // File Information
        // =========================================================

        public string OriginalFilePath { get; set; } = string.Empty;

        public string? DirectoryPath { get; set; }

        public string? FileName { get; set; }

        public long FileSizeBytes { get; set; }

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
