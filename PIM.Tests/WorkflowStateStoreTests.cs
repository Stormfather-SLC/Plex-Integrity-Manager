using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class WorkflowStateStoreTests
{
    [Fact]
    public void Scan_SurvivesRestartWithReviewDecisionsAndIdenticalPlanFingerprint()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        var profile = CreateProfile(workspace);
        var movies = new List<Movie>
        {
            CreateReadyMovie(workspace, profile),
            CreateAcceptedSuggestionMovie(workspace, profile),
            CreateNeedsReviewMovie(workspace, profile)
        };
        var fingerprint = PlanFingerprintBuilder.Build(
            movies,
            profile,
            LibraryGoal.OrganizeNewMovies);

        CreateStore(configuration).SaveMovies(movies, workspace.SourceRoot);
        var restarted = CreateStore(configuration);

        Assert.True(restarted.TryGetMovies(out var restored));
        Assert.Equal(
            fingerprint,
            PlanFingerprintBuilder.Build(
                restored,
                profile,
                LibraryGoal.OrganizeNewMovies));

        var accepted = restored.Single(movie => movie.Id == movies[1].Id);
        Assert.Equal(MetadataMatchOrigin.SpellCorrectedTitleYear, accepted.MetadataMatchOrigin);
        Assert.Equal("tt0000002", accepted.ImdbId);
        Assert.True(accepted.ApprovedForCommit);
        Assert.Equal(new[] { "Comedy", "Drama" }, accepted.Genres);

        var review = restored.Single(movie => movie.Id == movies[2].Id);
        Assert.True(review.NeedsReview);
        Assert.False(review.ApprovedForCommit);
        Assert.Equal(movies[2].ReviewReason, review.ReviewReason);
        Assert.True(review.HasMetadataReviewReason);
        Assert.Equal(MetadataLookupFailureType.FuzzyCandidateNeedsReview, review.MetadataLookupFailureType);
        Assert.Equal("Suggested Title", review.SuggestedTitle);
        Assert.True(review.CanAcceptMetadataSuggestion);
    }

    [Fact]
    public void DryRunApproval_SurvivesRestartAndStillMatchesOnlyTheSamePlan()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        var profile = CreateProfile(workspace);
        var movies = new List<Movie> { CreateReadyMovie(workspace, profile) };
        var fingerprint = PlanFingerprintBuilder.Build(
            movies,
            profile,
            LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        store.SaveMovies(movies, workspace.SourceRoot);
        store.SaveDryRun(
            new DryRunPreviewResult
            {
                TotalFiles = 1,
                ReadyToMoveCount = 1,
                Items = { new DryRunPreviewItem { FileName = "Ready.mkv", Action = "Move" } }
            },
            new DryRunApproval(
                profile.Id,
                profile.Revision,
                LibraryGoal.OrganizeNewMovies,
                fingerprint,
                DateTime.UtcNow));

        var restarted = CreateStore(configuration);
        var approval = restarted.GetDryRunApproval();
        var preview = restarted.GetDryRunPreview();

        Assert.NotNull(approval);
        Assert.True(approval!.Matches(profile, LibraryGoal.OrganizeNewMovies, fingerprint));
        Assert.False(approval.Matches(profile, LibraryGoal.ReorganizationMigration, fingerprint));
        Assert.Equal("Ready.mkv", Assert.Single(preview!.Items).FileName);
    }

    [Fact]
    public void DryRunApproval_ExpiresAfterMaxAgeEvenAcrossRestart()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        var store = CreateStore(configuration, clock);
        SaveScanAndApproval(store, workspace, clock.GetUtcNow().UtcDateTime);

        clock.Advance(DryRunApproval.MaxAge - TimeSpan.FromSeconds(1));
        Assert.NotNull(CreateStore(configuration, clock).GetDryRunApproval());

        clock.Advance(TimeSpan.FromSeconds(1));
        var restarted = CreateStore(configuration, clock);
        Assert.Null(restarted.GetDryRunApproval());
        Assert.Null(restarted.GetDryRunPreview());
        Assert.False(File.Exists(Path.Combine(workspace.StateDirectory, "dry-run.json")));
    }

    [Fact]
    public void DryRunApproval_CreatedInTheFutureIsTreatedAsExpired()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        var store = CreateStore(configuration, clock);
        SaveScanAndApproval(store, workspace, clock.GetUtcNow().UtcDateTime.AddHours(1));

        Assert.Null(CreateStore(configuration, clock).GetDryRunApproval());
    }

    [Fact]
    public void InvalidateDryRunApproval_RemovesApprovalFromDiskButKeepsScan()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        var store = CreateStore(configuration);
        SaveScanAndApproval(store, workspace, DateTime.UtcNow);

        store.InvalidateDryRunApproval();
        var restarted = CreateStore(configuration);

        Assert.Null(restarted.GetDryRunApproval());
        Assert.Null(restarted.GetDryRunPreview());
        Assert.True(restarted.TryGetMovies(out var movies));
        Assert.Single(movies);
    }

    [Fact]
    public void ClearMovies_RemovesScanAndApprovalFromDisk()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        var store = CreateStore(configuration);
        SaveScanAndApproval(store, workspace, DateTime.UtcNow);

        store.ClearMovies();
        var restarted = CreateStore(configuration);

        Assert.False(restarted.TryGetMovies(out _));
        Assert.Null(restarted.GetDryRunApproval());
    }

    [Fact]
    public void SourceFolderChangedWhileStopped_DiscardsScanAndApproval()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        SaveScanAndApproval(CreateStore(configuration), workspace, DateTime.UtcNow);

        configuration["PIM:ScanPath"] = Path.Combine(workspace.Root, "Other Source");
        var restarted = CreateStore(configuration);

        Assert.False(restarted.TryGetMovies(out _));
        Assert.Null(restarted.GetDryRunApproval());
        Assert.False(File.Exists(Path.Combine(workspace.StateDirectory, "scan.json")));
    }

    [Fact]
    public void CorruptScanFile_IsSetAsideAndNothingIsRestored()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        SaveScanAndApproval(CreateStore(configuration), workspace, DateTime.UtcNow);
        var scanPath = Path.Combine(workspace.StateDirectory, "scan.json");
        File.WriteAllText(scanPath, "{ \"SchemaVersion\": 1, \"Movies\": [ { \"Title\": ");

        var restarted = CreateStore(configuration);

        Assert.False(restarted.TryGetMovies(out _));
        Assert.Null(restarted.GetDryRunApproval());
        Assert.False(File.Exists(scanPath));
        Assert.Single(Directory.GetFiles(workspace.StateDirectory, "scan.json.corrupt-*"));
    }

    [Fact]
    public void ScanWithIncompleteMovie_IsRejectedAsCorrupt()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        Directory.CreateDirectory(workspace.StateDirectory);
        File.WriteAllText(
            Path.Combine(workspace.StateDirectory, "scan.json"),
            $$"""
            {
              "SchemaVersion": 1,
              "SourceRoot": {{System.Text.Json.JsonSerializer.Serialize(workspace.SourceRoot)}},
              "Movies": [ { "Title": "No Source Path" } ]
            }
            """);

        Assert.False(CreateStore(configuration).TryGetMovies(out _));
    }

    [Fact]
    public void UnsupportedSchemaVersion_IsNotRestored()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        SaveScanAndApproval(CreateStore(configuration), workspace, DateTime.UtcNow);
        var scanPath = Path.Combine(workspace.StateDirectory, "scan.json");
        File.WriteAllText(
            scanPath,
            File.ReadAllText(scanPath).Replace(
                "\"SchemaVersion\":1",
                "\"SchemaVersion\":99"));

        Assert.False(CreateStore(configuration).TryGetMovies(out _));
    }

    [Fact]
    public void DryRunFileWithoutScan_IsIgnoredAndRemoved()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var configuration = CreateConfiguration(workspace);
        SaveScanAndApproval(CreateStore(configuration), workspace, DateTime.UtcNow);
        File.Delete(Path.Combine(workspace.StateDirectory, "scan.json"));

        var restarted = CreateStore(configuration);

        Assert.Null(restarted.GetDryRunApproval());
        Assert.False(File.Exists(Path.Combine(workspace.StateDirectory, "dry-run.json")));
    }

    [Fact]
    public void DependencyInjection_ResolvesStoreAsSingletonUsingConfiguredDirectory()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CreateConfiguration(workspace));
        services.AddSingleton<IWorkflowStateStore, JsonWorkflowStateStore>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        var store = provider.GetRequiredService<IWorkflowStateStore>();

        Assert.Same(store, provider.GetRequiredService<IWorkflowStateStore>());
        Assert.Equal(
            Path.GetFullPath(workspace.StateDirectory),
            ((JsonWorkflowStateStore)store).Directory);
    }

    [Fact]
    public void NoSavedState_StartsEmpty()
    {
        using var workspace = new TempWorkspace("PIM-WorkflowState-Tests");
        var store = CreateStore(CreateConfiguration(workspace));

        Assert.False(store.TryGetMovies(out var movies));
        Assert.Empty(movies);
        Assert.Null(store.GetDryRunApproval());
        Assert.Null(store.GetDryRunPreview());
    }

    private static void SaveScanAndApproval(
        JsonWorkflowStateStore store,
        TempWorkspace workspace,
        DateTime approvalCreatedUtc)
    {
        var profile = CreateProfile(workspace);
        var movies = new List<Movie> { CreateReadyMovie(workspace, profile) };
        store.SaveMovies(movies, workspace.SourceRoot);
        store.SaveDryRun(
            new DryRunPreviewResult { TotalFiles = 1 },
            new DryRunApproval(
                profile.Id,
                profile.Revision,
                LibraryGoal.OrganizeNewMovies,
                PlanFingerprintBuilder.Build(movies, profile, LibraryGoal.OrganizeNewMovies),
                approvalCreatedUtc));
    }

    private static IConfiguration CreateConfiguration(TempWorkspace workspace)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PIM:ScanPath"] = workspace.SourceRoot,
                ["PIM:WorkflowStateDirectory"] = workspace.StateDirectory
            })
            .Build();
    }

    private static JsonWorkflowStateStore CreateStore(
        IConfiguration configuration,
        TimeProvider? clock = null)
    {
        return new JsonWorkflowStateStore(
            configuration,
            logger: null,
            clock ?? TimeProvider.System);
    }

    private static DestinationProfile CreateProfile(TempWorkspace workspace)
    {
        return new DestinationProfile
        {
            Name = "State Profile",
            DestinationRoot = workspace.DestinationRoot
        };
    }

    private static Movie CreateReadyMovie(TempWorkspace workspace, DestinationProfile profile)
    {
        return new Movie
        {
            Title = "Ready Movie",
            Year = 2024,
            ImdbId = "tt0000001",
            OriginalFilePath = Path.Combine(workspace.SourceRoot, "Ready.Movie.2024.mkv"),
            FileName = "Ready.Movie.2024.mkv",
            FileSizeBytes = 1234,
            MetadataFetched = true,
            MatchConfidence = 97.35,
            TargetPath = Path.Combine(
                profile.DestinationRoot,
                "Ready Movie (2024) {imdb-tt0000001}",
                "Ready Movie (2024) {imdb-tt0000001}.mkv"),
            DestinationProfileId = profile.Id,
            DestinationProfileRevision = profile.Revision,
            PlannedLibraryGoal = LibraryGoal.OrganizeNewMovies,
            ApprovedForCommit = true,
            Status = "IMDb ID Match"
        };
    }

    private static Movie CreateAcceptedSuggestionMovie(
        TempWorkspace workspace,
        DestinationProfile profile)
    {
        return new Movie
        {
            Title = "Accepted Movie",
            Year = 1999,
            ImdbId = "tt0000002",
            OriginalFilePath = Path.Combine(workspace.SourceRoot, "Acepted.Movie.mkv"),
            FileName = "Acepted.Movie.mkv",
            MetadataFetched = true,
            MatchConfidence = 100,
            MetadataMatchOrigin = MetadataMatchOrigin.SpellCorrectedTitleYear,
            Genres = new List<string> { "Comedy", "Drama" },
            PrimaryGenre = "Comedy",
            TargetPath = Path.Combine(profile.DestinationRoot, "accepted.mkv"),
            DestinationProfileId = profile.Id,
            DestinationProfileRevision = profile.Revision,
            PlannedLibraryGoal = LibraryGoal.OrganizeNewMovies,
            ApprovedForCommit = true
        };
    }

    private static Movie CreateNeedsReviewMovie(
        TempWorkspace workspace,
        DestinationProfile profile)
    {
        var movie = new Movie
        {
            Title = "Uncertain",
            OriginalFilePath = Path.Combine(workspace.SourceRoot, "Uncertain.mkv"),
            FileName = "Uncertain.mkv",
            SuggestedTitle = "Suggested Title",
            SuggestedYear = 2001,
            SuggestedImdbId = "tt0000003",
            DestinationProfileId = profile.Id,
            DestinationProfileRevision = profile.Revision,
            PlannedLibraryGoal = LibraryGoal.OrganizeNewMovies
        };
        movie.SetMetadataReview(
            "Possible OMDb match: Suggested Title (2001)",
            MetadataLookupFailureType.FuzzyCandidateNeedsReview,
            "No exact title match.");
        movie.RequireReview("Duplicate needs a human decision");

        return movie;
    }
}
