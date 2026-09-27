using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class MvpWorkflowRegressionTests
{
    [Fact]
    public void Rebuild_DestinationAndWorkflowChangesReuseCompletedMetadata()
    {
        var planner = CreatePlanner(PlexLibraryConflictResult.NoConflict());
        var movie = CreateIdentifiedMovie();
        var movies = new List<Movie> { movie };
        var firstProfile = CreateProfile("First");

        planner.Rebuild(
            movies,
            firstProfile,
            Path.GetTempPath(),
            LibraryGoal.OrganizeNewMovies);
        var firstTarget = movie.TargetPath;

        var secondProfile = CreateProfile("Second");
        secondProfile.Id = firstProfile.Id;
        secondProfile.Revision = firstProfile.Revision + 1;

        planner.Rebuild(
            movies,
            secondProfile,
            Path.GetTempPath(),
            LibraryGoal.ReorganizationMigration);

        Assert.NotEqual(firstTarget, movie.TargetPath);
        Assert.StartsWith(
            Path.GetFullPath(secondProfile.DestinationRoot),
            movie.TargetPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(movie.MetadataFetched);
        Assert.Equal("Trustworthy Movie", movie.Title);
        Assert.Equal(2024, movie.Year);
        Assert.Equal("tt1234567", movie.ImdbId);
        Assert.Equal(100, movie.MatchConfidence);
        Assert.Equal(
            LibraryGoal.ReorganizationMigration,
            movie.PlannedLibraryGoal);
    }

    [Fact]
    public void Rebuild_WorkflowChangeRecalculatesPlexPolicyWithoutMetadataRefresh()
    {
        var plexPath = Path.Combine(
            Path.GetTempPath(),
            "Plex",
            "Trustworthy Movie.mkv");
        var planner = CreatePlanner(PlexLibraryConflictResult.Conflict(
            PlexLibraryConflictType.SameImdbIdDifferentPath,
            "Plex already contains the same IMDb ID.",
            plexPath));
        var movie = CreateIdentifiedMovie();
        var movies = new List<Movie> { movie };
        var profile = CreateProfile("Workflow");

        planner.Rebuild(
            movies,
            profile,
            Path.GetTempPath(),
            LibraryGoal.OrganizeNewMovies);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.True(movie.NeedsReview);

        planner.Rebuild(
            movies,
            profile,
            Path.GetTempPath(),
            LibraryGoal.ReorganizationMigration);

        Assert.False(movie.HasPlexLibraryConflict);
        Assert.True(movie.IsPlexTrackedMigration);
        Assert.False(movie.NeedsReview);
        Assert.True(movie.ApprovedForCommit);
        Assert.True(movie.MetadataFetched);
    }

    [Fact]
    public async Task AcceptSuggestion_UsesSelectedIdentityAndRerunsDownstreamPlan()
    {
        var movie = CreateIdentifiedMovie();
        movie.Title = "Trustwothy Movie";
        movie.ImdbId = null;
        movie.MetadataFetched = false;
        movie.SuggestedTitle = "Trustworthy Movie";
        movie.SuggestedYear = 2024;
        movie.SuggestedImdbId = "tt1234567";
        movie.SetMetadataReview(
            "Possible OMDb match: Trustworthy Movie (2024)",
            MetadataLookupFailureType.FuzzyCandidateNeedsReview);
        movie.RequireReview("Destination conflict: The target already exists.");

        var metadata = new CountingMetadataService();
        var plan = new CountingPlanService();
        var service = new MetadataSuggestionService(metadata, plan);

        var accepted = await service.AcceptAsync(
            movie,
            new List<Movie> { movie },
            CreateProfile("Suggestion"),
            Path.GetTempPath(),
            LibraryGoal.OrganizeNewMovies);

        Assert.True(accepted);
        Assert.Equal(1, metadata.CallCount);
        Assert.Equal(1, plan.CallCount);
        Assert.Equal("Trustworthy Movie", movie.Title);
        Assert.Equal(2024, movie.Year);
        Assert.Equal("tt1234567", movie.ImdbId);
        Assert.True(movie.MetadataFetched);
        Assert.DoesNotContain("Possible OMDb match", movie.ReviewReason);
        Assert.Contains("Destination conflict:", movie.ReviewReason);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public async Task AcceptSuggestion_DownstreamDestinationAndPlexConflictsRemainBlocked()
    {
        var movie = CreateIdentifiedMovie();
        movie.ImdbId = null;
        movie.MetadataFetched = false;
        movie.SuggestedTitle = "Trustworthy Movie";
        movie.SuggestedYear = 2024;
        movie.SuggestedImdbId = "tt1234567";
        movie.SetMetadataReview(
            "Possible OMDb match requires review",
            MetadataLookupFailureType.FuzzyCandidateNeedsReview);
        var destination = new FixedDestinationService(
            DestinationConflictResult.Conflict(
                DestinationConflictType.TargetFileAlreadyExists,
                "The exact target already exists.",
                Path.Combine(Path.GetTempPath(), "existing-target.mkv")));
        var configuration = new ConfigurationBuilder().Build();
        var rename = new RenameService(
            destination,
            new DestinationPathBuilder(),
            configuration,
            new ScanProgress(),
            new SourceCleanupStatus());
        var conflicts = new MovieConflictDetectionService(
            destination,
            new StubPlexConflictService(PlexLibraryConflictResult.Conflict(
                PlexLibraryConflictType.SameImdbIdDifferentPath,
                "Plex already contains this IMDb identity.",
                Path.Combine(Path.GetTempPath(), "plex-existing.mkv"))));
        var planner = new MoviePlanService(
            new DuplicateService(),
            rename,
            conflicts);
        var service = new MetadataSuggestionService(
            new CountingMetadataService(),
            planner);

        await service.AcceptAsync(
            movie,
            new List<Movie> { movie },
            CreateProfile("Suggestion-Conflicts"),
            Path.GetTempPath(),
            LibraryGoal.OrganizeNewMovies);

        Assert.True(movie.HasDestinationConflict);
        Assert.True(movie.HasPlexLibraryConflict);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Contains("Destination conflict:", movie.ReviewReason);
        Assert.Contains("Plex library conflict:", movie.ReviewReason);
    }

    [Fact]
    public void DryRunPreview_ReviewItemIncludesEvidenceAndProposedDestination()
    {
        var movie = CreateIdentifiedMovie();
        movie.TargetPath = Path.Combine(Path.GetTempPath(), "proposed.mkv");
        movie.SuggestedTitle = "Suggested Movie";
        movie.SuggestedYear = 2024;
        movie.SuggestedImdbId = "tt7654321";
        movie.MatchConfidence = 82;
        movie.SetMetadataReview(
            "Possible OMDb match requires review",
            MetadataLookupFailureType.FuzzyCandidateNeedsReview,
            "Another candidate scored closely.");

        var result = new DryRunPreviewService().BuildPreview(
            new[] { movie },
            LibraryGoal.OrganizeNewMovies);
        var item = Assert.Single(result.Items);

        Assert.Equal("Needs Review", item.Action);
        Assert.Equal(movie.TargetPath, item.TargetPath);
        Assert.Equal(movie.OriginalFilePath, item.OriginalFilePath);
        Assert.Equal("Trustworthy Movie", item.ParsedTitle);
        Assert.Equal(2024, item.ParsedYear);
        Assert.Equal("tt1234567", item.ParsedImdbId);
        Assert.Equal(82, item.MetadataConfidence);
        Assert.Equal("Suggested Movie", item.SuggestedTitle);
        Assert.Equal("Another candidate scored closely.", item.MetadataFailureDetail);
    }

    [Fact]
    public void PlanFingerprint_ChangesWhenAnyCriticalPlanCategoryChanges()
    {
        AssertFingerprintChanges(movie => movie.OriginalFilePath += ".changed");
        AssertFingerprintChanges(movie => movie.Title = "Changed Identity");
        AssertFingerprintChanges(movie => movie.ImdbId = "tt7654321");
        AssertFingerprintChanges(movie => movie.TargetPath += ".changed");
        AssertFingerprintChanges(movie => movie.ApprovedForCommit = false);
        AssertFingerprintChanges(movie => movie.RequireReview("New uncertainty"));
        AssertFingerprintChanges(movie => movie.ErrorMessage = "New error");
        AssertFingerprintChanges(movie => movie.HasDestinationConflict = true);
        AssertFingerprintChanges(movie => movie.HasPlexLibraryConflict = true);

        var movie = CreateIdentifiedMovie();
        var profile = CreateProfile("Fingerprint");
        var original = PlanFingerprintBuilder.Build(
            new[] { movie },
            profile,
            LibraryGoal.OrganizeNewMovies);

        profile.Revision++;
        Assert.NotEqual(
            original,
            PlanFingerprintBuilder.Build(
                new[] { movie },
                profile,
                LibraryGoal.OrganizeNewMovies));
        Assert.NotEqual(
            original,
            PlanFingerprintBuilder.Build(
                new[] { movie },
                CreateProfile("Fingerprint"),
                LibraryGoal.ReorganizationMigration));
    }

    [Fact]
    public void DryRunApproval_MatchesOnlyExactCurrentPlan()
    {
        var profile = CreateProfile("Approval");
        var approval = new DryRunApproval(
            profile.Id,
            profile.Revision,
            LibraryGoal.OrganizeNewMovies,
            "FINGERPRINT",
            DateTime.UtcNow);

        Assert.True(approval.Matches(
            profile,
            LibraryGoal.OrganizeNewMovies,
            "FINGERPRINT"));
        Assert.False(approval.Matches(
            profile,
            LibraryGoal.ReorganizationMigration,
            "FINGERPRINT"));
        Assert.False(approval.Matches(
            profile,
            LibraryGoal.OrganizeNewMovies,
            "CHANGED"));

        profile.Revision++;
        Assert.False(approval.Matches(
            profile,
            LibraryGoal.OrganizeNewMovies,
            "FINGERPRINT"));
    }

    [Fact]
    public void SettingsChange_SourceClearsScan_DestinationOrWorkflowOnlyRebuildsPlan()
    {
        var sourceChange = WorkflowSettingsChange.Evaluate(
            @"C:\Source-A",
            @"C:\Source-B",
            @"C:\Destination",
            @"C:\Destination",
            LibraryGoal.OrganizeNewMovies,
            LibraryGoal.OrganizeNewMovies);
        var destinationChange = WorkflowSettingsChange.Evaluate(
            @"C:\Source",
            @"C:\Source",
            @"C:\Destination-A",
            @"C:\Destination-B",
            LibraryGoal.OrganizeNewMovies,
            LibraryGoal.OrganizeNewMovies);
        var workflowChange = WorkflowSettingsChange.Evaluate(
            @"C:\Source",
            @"C:\Source",
            @"C:\Destination",
            @"C:\Destination",
            LibraryGoal.OrganizeNewMovies,
            LibraryGoal.ReorganizationMigration);

        Assert.True(sourceChange.ClearScan);
        Assert.False(sourceChange.RebuildExistingPlan);
        Assert.False(destinationChange.ClearScan);
        Assert.True(destinationChange.RebuildExistingPlan);
        Assert.False(workflowChange.ClearScan);
        Assert.True(workflowChange.RebuildExistingPlan);
    }

    [Fact]
    public void DestinationConflict_SourceAndTargetSameFile_IsBlocked()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PIM-MVP-Same-Path",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "movie.mkv");
            File.WriteAllText(path, "movie");
            var movie = CreateIdentifiedMovie();
            movie.OriginalFilePath = path;
            movie.TargetPath = path;

            var result = new DestinationConflictService(new ScanProgress())
                .Check(movie, root);

            Assert.True(result.HasConflict);
            Assert.Equal(
                DestinationConflictType.SourceAndTargetAreSame,
                result.ConflictType);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MoviePlanService CreatePlanner(
        PlexLibraryConflictResult plexResult)
    {
        var destination = new NoConflictDestinationService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PIM:RemoveEmptySourceFolders"] = "false"
            })
            .Build();
        var rename = new RenameService(
            destination,
            new DestinationPathBuilder(),
            configuration,
            new ScanProgress(),
            new SourceCleanupStatus());
        var conflicts = new MovieConflictDetectionService(
            destination,
            new StubPlexConflictService(plexResult));

        return new MoviePlanService(
            new DuplicateService(),
            rename,
            conflicts);
    }

    private static DestinationProfile CreateProfile(string suffix)
    {
        return new DestinationProfile
        {
            Name = suffix,
            DestinationRoot = Path.Combine(
                Path.GetTempPath(),
                "PIM-MVP-Plan",
                suffix,
                Guid.NewGuid().ToString("N"))
        };
    }

    private static Movie CreateIdentifiedMovie()
    {
        return new Movie
        {
            Title = "Trustworthy Movie",
            Year = 2024,
            ImdbId = "tt1234567",
            FileName = "Trustworthy.Movie.2024.mkv",
            OriginalFilePath = Path.Combine(
                Path.GetTempPath(),
                "Source",
                "Trustworthy.Movie.2024.mkv"),
            FileSizeBytes = 100 * 1024 * 1024,
            MetadataFetched = true,
            MatchConfidence = 100,
            Status = "Metadata Enriched"
        };
    }

    private static void AssertFingerprintChanges(Action<Movie> change)
    {
        var movie = CreateIdentifiedMovie();
        movie.TargetPath = Path.Combine(Path.GetTempPath(), "target.mkv");
        movie.ApprovedForCommit = true;
        movie.PlannedLibraryGoal = LibraryGoal.OrganizeNewMovies;
        var profile = CreateProfile("Fingerprint-Movie");
        var before = PlanFingerprintBuilder.Build(
            new[] { movie },
            profile,
            LibraryGoal.OrganizeNewMovies);

        change(movie);

        Assert.NotEqual(
            before,
            PlanFingerprintBuilder.Build(
                new[] { movie },
                profile,
                LibraryGoal.OrganizeNewMovies));
    }

    private sealed class CountingMetadataService : IMetadataService
    {
        public int CallCount { get; private set; }

        public Task EnrichAsync(Movie movie)
        {
            CallCount++;
            movie.ClearMetadataReviewReasons();
            movie.MetadataFetched = true;
            movie.Status = "IMDb ID Match";
            return Task.CompletedTask;
        }
    }

    private sealed class CountingPlanService : IMoviePlanService
    {
        public int CallCount { get; private set; }

        public void Rebuild(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot,
            LibraryGoal libraryGoal)
        {
            CallCount++;
        }
    }

    private sealed class StubPlexConflictService : IPlexLibraryConflictService
    {
        private readonly PlexLibraryConflictResult _result;

        public StubPlexConflictService(PlexLibraryConflictResult result)
        {
            _result = result;
        }

        public PlexLibraryConflictResult Check(Movie movie) => _result;
    }

    private sealed class NoConflictDestinationService : IDestinationConflictService
    {
        public void InvalidateCache()
        {
        }

        public void RecordDestinationEntry(string path)
        {
        }

        public DestinationConflictResult Check(
            Movie movie,
            string outputPath,
            bool refresh = false)
        {
            return DestinationConflictResult.NoConflict();
        }
    }

    private sealed class FixedDestinationService : IDestinationConflictService
    {
        private readonly DestinationConflictResult _result;

        public FixedDestinationService(DestinationConflictResult result)
        {
            _result = result;
        }

        public void InvalidateCache()
        {
        }

        public void RecordDestinationEntry(string path)
        {
        }

        public DestinationConflictResult Check(
            Movie movie,
            string outputPath,
            bool refresh = false)
        {
            return _result;
        }
    }
}
