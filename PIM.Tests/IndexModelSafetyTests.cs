using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Web.Pages;
using PIM.Web.Services;
using Xunit;

namespace PIM.Tests;

public sealed class IndexModelSafetyTests
{
    private const string MovieScanCacheKey = "MovieScan";

    [Fact]
    public async Task LiveCommitWithoutMatchingDryRun_IsRejectedBeforeRenameService()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var profile = CreateProfile();
        var movie = CreateMovie(profile, LibraryGoal.OrganizeNewMovies);
        cache.Set(MovieScanCacheKey, new List<Movie> { movie });
        var rename = new RecordingRenameService();
        var model = CreateModel(
            cache,
            profile,
            LibraryGoal.OrganizeNewMovies,
            rename: rename);
        model.DryRun = false;

        await model.OnPostCommitAsync();

        Assert.Equal(0, rename.ExecuteCount);
        Assert.Contains(
            "does not have a matching dry-run approval",
            model.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task ProfileChange_RebuildsTargetWithoutMetadataCall()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var oldProfile = CreateProfile();
        var currentProfile = CreateProfile();
        var movie = CreateMovie(oldProfile, LibraryGoal.OrganizeNewMovies);
        var originalTarget = movie.TargetPath;
        cache.Set(MovieScanCacheKey, new List<Movie> { movie });
        var metadata = new CountingMetadataService();
        var plan = new RecordingPlanService();
        var model = CreateModel(
            cache,
            currentProfile,
            LibraryGoal.OrganizeNewMovies,
            metadata: metadata,
            plan: plan);

        await model.OnGetAsync();

        Assert.Equal(1, plan.CallCount);
        Assert.Equal(0, metadata.CallCount);
        Assert.NotEqual(originalTarget, movie.TargetPath);
        Assert.StartsWith(
            currentProfile.DestinationRoot,
            movie.TargetPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkflowChange_RecalculatesPlanWithoutMetadataCall()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var profile = CreateProfile();
        var movie = CreateMovie(profile, LibraryGoal.OrganizeNewMovies);
        cache.Set(MovieScanCacheKey, new List<Movie> { movie });
        var metadata = new CountingMetadataService();
        var plan = new RecordingPlanService();
        var model = CreateModel(
            cache,
            profile,
            LibraryGoal.ReorganizationMigration,
            metadata: metadata,
            plan: plan);

        await model.OnGetAsync();

        Assert.Equal(1, plan.CallCount);
        Assert.Equal(0, metadata.CallCount);
        Assert.Equal(
            LibraryGoal.ReorganizationMigration,
            movie.PlannedLibraryGoal);
    }

    private static IndexModel CreateModel(
        IMemoryCache cache,
        DestinationProfile profile,
        LibraryGoal workflow,
        CountingMetadataService? metadata = null,
        RecordingRenameService? rename = null,
        RecordingPlanService? plan = null)
    {
        metadata ??= new CountingMetadataService();
        rename ??= new RecordingRenameService();
        plan ??= new RecordingPlanService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PIM:ScanPath"] = Path.Combine(Path.GetTempPath(), "PIM-Source"),
                ["PIM:LibraryGoal"] = workflow.ToString(),
                ["PIM:MetadataDelayMs"] = "0"
            })
            .Build();
        var model = new IndexModel(
            new EmptyScanner(),
            new NoOpParser(),
            metadata,
            rename,
            configuration,
            cache,
            new ScanProgress(),
            new PreviewTreeService(),
            new EmptyDryRunPreviewService(),
            new FixedProfileStore(profile),
            plan,
            new NoOpSuggestionService(),
            NullLogger<IndexModel>.Instance);
        var httpContext = new DefaultHttpContext();
        model.PageContext = new PageContext
        {
            HttpContext = httpContext
        };
        model.TempData = new TempDataDictionary(
            httpContext,
            new InMemoryTempDataProvider());

        return model;
    }

    private static DestinationProfile CreateProfile()
    {
        return new DestinationProfile
        {
            Name = "Test Profile",
            DestinationRoot = Path.Combine(
                Path.GetTempPath(),
                "PIM-Index-Tests",
                Guid.NewGuid().ToString("N"))
        };
    }

    private static Movie CreateMovie(
        DestinationProfile profile,
        LibraryGoal plannedWorkflow)
    {
        return new Movie
        {
            Title = "Handler Movie",
            Year = 2024,
            ImdbId = "tt1234567",
            OriginalFilePath = Path.Combine(
                Path.GetTempPath(),
                "PIM-Source",
                "Handler.Movie.2024.mkv"),
            FileName = "Handler.Movie.2024.mkv",
            MetadataFetched = true,
            MatchConfidence = 100,
            TargetPath = Path.Combine(
                profile.DestinationRoot,
                "old-target.mkv"),
            DestinationProfileId = profile.Id,
            DestinationProfileRevision = profile.Revision,
            PlannedLibraryGoal = plannedWorkflow,
            ApprovedForCommit = true
        };
    }

    private sealed class CountingMetadataService : IMetadataService
    {
        public int CallCount { get; private set; }

        public Task EnrichAsync(Movie movie)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPlanService : IMoviePlanService
    {
        public int CallCount { get; private set; }

        public void Rebuild(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot,
            LibraryGoal libraryGoal)
        {
            CallCount++;

            foreach (var movie in movies)
            {
                movie.TargetPath = Path.Combine(
                    profile.DestinationRoot,
                    movie.FileName ?? "movie.mkv");
                movie.DestinationProfileId = profile.Id;
                movie.DestinationProfileRevision = profile.Revision;
                movie.PlannedLibraryGoal = libraryGoal;
            }
        }
    }

    private sealed class RecordingRenameService : IRenameService
    {
        public int ExecuteCount { get; private set; }

        public void GeneratePreview(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot)
        {
        }

        public void ExecuteChanges(
            List<Movie> movies,
            bool dryRun,
            string destinationRoot)
        {
            ExecuteCount++;
        }
    }

    private sealed class FixedProfileStore : IDestinationProfileStore
    {
        private readonly DestinationProfile _profile;

        public FixedProfileStore(DestinationProfile profile)
        {
            _profile = profile;
        }

        public IReadOnlyList<DestinationProfile> GetProfiles() => new[] { _profile };

        public DestinationProfile GetActiveProfile() => _profile;

        public DestinationProfile? GetProfile(Guid profileId) =>
            profileId == _profile.Id ? _profile : null;

        public DestinationProfile Save(DestinationProfile profile) => profile;

        public bool Delete(Guid profileId) => false;

        public bool SetActive(Guid profileId) => profileId == _profile.Id;
    }

    private sealed class EmptyScanner : IFileScanner
    {
        public List<Movie> Scan(string rootPath, string? excludedRootPath = null) => new();

        public List<string> GetFiles(string rootPath, string? excludedRootPath = null) => new();
    }

    private sealed class NoOpParser : IFileNameParser
    {
        public void Parse(Movie movie)
        {
        }
    }

    private sealed class EmptyDryRunPreviewService : IDryRunPreviewService
    {
        public DryRunPreviewResult BuildPreview(
            IEnumerable<Movie> movies,
            LibraryGoal libraryGoal = LibraryGoal.OrganizeNewMovies)
        {
            return new DryRunPreviewResult { LibraryGoal = libraryGoal };
        }
    }

    private sealed class NoOpSuggestionService : IMetadataSuggestionService
    {
        public Task<bool> AcceptAsync(
            Movie movie,
            List<Movie> allMovies,
            DestinationProfile profile,
            string sourceRoot,
            LibraryGoal libraryGoal)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> _values = new();

        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>(_values);

        public void SaveTempData(
            HttpContext context,
            IDictionary<string, object> values)
        {
            _values = new Dictionary<string, object>(values);
        }
    }
}
