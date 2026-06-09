using System;
using System.Collections.Generic;

namespace PIM.Core.Models
{
    public class Movie
    {
        // =========================================================
        // 🔑 Identity
        // =========================================================

        public Guid Id { get; set; } = Guid.NewGuid();


        // =========================================================
        // 🔁 Duplicate / Decision Logic
        // =========================================================

        public Guid? DuplicateGroupId { get; set; }

        public bool IsDuplicate { get; set; }

        public bool KeepRecommended { get; set; }

        /// <summary>
        /// Indicates whether this movie has been approved for
        /// processing during the Commit step.
        /// </summary>
        public bool ApprovedForCommit { get; set; }

        /// <summary>
        /// Indicates this is a valid alternate version
        /// (Director's Cut, Extended, etc.)
        /// </summary>
        public bool IsAlternateVersion { get; set; }


        // =========================================================
        // 🔵 Review System (NEW)
        // =========================================================

        /// <summary>
        /// Indicates the system is not confident and requires human review.
        /// </summary>
        public bool NeedsReview { get; set; }

        /// <summary>
        /// Explains WHY the item needs review (for UI display/debugging).
        /// </summary>
        public string? ReviewReason { get; set; }


        // =========================================================
        // 🔴 Error System (NEW)
        // =========================================================

        /// <summary>
        /// Indicates a critical error occurred (OMDb failure, parsing failure, etc.)
        /// </summary>
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);


        // =========================================================
        // 🔮 Future Decision Enhancements
        // =========================================================

        public string? VersionTag { get; set; }

        public bool IsManuallyKept { get; set; }


        // =========================================================
        // 🧠 Basic Identification
        // =========================================================

        public string? Title { get; set; }

        public int? Year { get; set; }

        public string? ImdbId { get; set; }


        // =========================================================
        // 🔍 Future: Fuzzy Matching
        // =========================================================

        public string? SuggestedTitle { get; set; }

        public double? MatchConfidence { get; set; }

        public bool IsFuzzyMatch { get; set; }

        public bool MetadataMatchedByImdbId { get; set; }


        // =========================================================
        // 📁 File Information
        // =========================================================

        public string OriginalFilePath { get; set; } = string.Empty;

        public string? DirectoryPath { get; set; }

        public string? FileName { get; set; }

        public long FileSizeBytes { get; set; }

        public string? OriginalPath => OriginalFilePath;


        // =========================================================
        // ⚙️ Processing State
        // =========================================================

        public bool MetadataFetched { get; set; }

        public List<int> CandidateYears { get; set; } = new();


        // =========================================================
        // 📦 Output / Rename
        // =========================================================

        public string? NormalizedFolderName { get; set; }

        public string? NormalizedFileName { get; set; }

        public string? TargetPath { get; set; }


        // =========================================================
        // ⚠️ Status / Errors
        // =========================================================

        public string Status { get; set; } = "Pending";

        public string? ErrorMessage { get; set; }


        // =========================================================
        // 🎨 UI Helper (NEW — CRITICAL)
        // =========================================================

        /// <summary>
        /// Returns the UI color classification for this movie.
        /// Used for row coloring in the UI.
        /// </summary>
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
        // 🧩 Helper Methods
        // =========================================================
        private bool ShouldIncludeEditionTag(string? versionTag)
        {
            if (string.IsNullOrWhiteSpace(versionTag))
                return false;

            var normalized = versionTag.Trim();

            return !normalized.Equals("Alternate Version", StringComparison.OrdinalIgnoreCase);
        }
        public string GetNormalizedFolderName()
        {
            if (string.IsNullOrWhiteSpace(Title) || Year == null || string.IsNullOrWhiteSpace(ImdbId))
                throw new InvalidOperationException("Movie is missing required metadata.");

            var editionPart = ShouldIncludeEditionTag(VersionTag)
                ? $" {{edition-{VersionTag!.Trim()}}}"
                : string.Empty;

            return $"{Title} ({Year}){editionPart} {{imdb-{ImdbId}}}";
        }

        public string GetNormalizedFileName(string extension)
        {
            var folderName = GetNormalizedFolderName();
            return $"{folderName}{extension}";
        }
    }
}