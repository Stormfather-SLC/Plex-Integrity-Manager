using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Web.Models;
using PIM.Web.Services;

namespace PIM.Web.Pages
{
    /// <summary>
    /// Main PIM workflow: scan, identify, preview, dry run, and commit.
    /// </summary>
    public class IndexModel : PageModel
    {
        private const string MovieScanCacheKey = "MovieScan";
        private const string DryRunPreviewCacheKey = "DryRunPreview";
        private const int CacheDurationMinutes = 30;
        private const int DefaultMetadataDelayMs = 250;

        private readonly IFileScanner _scanner;
        private readonly IFileNameParser _parser;
        private readonly IMetadataService _metadata;
        private readonly IDuplicateService _duplicates;
        private readonly IRenameService _rename;
        private readonly IConfiguration _config;
        private readonly IMemoryCache _cache;
        private readonly ScanProgress _progress;
        private readonly PreviewTreeService _treeService;
        private readonly IDryRunPreviewService _dryRunPreviewService;
        private readonly IMovieConflictDetectionService _conflictDetection;
        private readonly IDestinationProfileStore _profileStore;

        [BindProperty(SupportsGet = true)]
        public bool ShowOnlyRecommended { get; set; }

        [BindProperty]
        public string ScanPath { get; set; } = string.Empty;

        [BindProperty]
        public string OutputPath { get; set; } = string.Empty;

        [BindProperty]
        public bool DryRun { get; set; } = true;

        public List<Movie> Movies { get; set; } = new();

        public FileNode? PreviewTree { get; set; }

        public DryRunPreviewResult? DryRunPreview { get; set; }

        public DestinationProfile ActiveDestinationProfile { get; set; } = new();

        public IndexModel(
            IFileScanner scanner,
            IFileNameParser parser,
            IMetadataService metadata,
            IDuplicateService duplicates,
            IRenameService rename,
            IConfiguration config,
            IMemoryCache cache,
            ScanProgress progress,
            PreviewTreeService treeService,
            IDryRunPreviewService dryRunPreviewService,
            IMovieConflictDetectionService conflictDetection,
            IDestinationProfileStore profileStore)
        {
            _scanner = scanner;
            _parser = parser;
            _metadata = metadata;
            _duplicates = duplicates;
            _rename = rename;
            _config = config;
            _cache = cache;
            _progress = progress;
            _treeService = treeService;
            _dryRunPreviewService = dryRunPreviewService;
            _conflictDetection = conflictDetection;
            _profileStore = profileStore;
        }

        public Task OnGetAsync(bool showOnlyRecommended = false)
        {
            ShowOnlyRecommended = showOnlyRecommended;

            LoadConfiguredPaths();
            LoadCachedMovies(showOnlyRecommended);
            LoadCachedDryRunPreview();

            return Task.CompletedTask;
        }

        public JsonResult OnGetProgress()
        {
            return new JsonResult(new
            {
                total = _progress.Total,
                processed = _progress.Processed,
                currentFile = _progress.CurrentFile,
                isRunning = _progress.IsRunning
            });
        }

        public IActionResult OnPostRescan()
        {
            _cache.Remove(DryRunPreviewCacheKey);

            var rootPath = _config["PIM:ScanPath"] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return new JsonResult(new
                {
                    started = false,
                    message = "A source folder must be configured before scanning."
                });
            }

            ResetProgress();
            _progress.IsRunning = true;

            _ = Task.Run(() =>
            {
                try
                {
                    var files = _scanner.GetFiles(rootPath);
                    _progress.Total = files.Count;

                    var movies = new List<Movie>();

                    foreach (var file in files)
                    {
                        var fileInfo = new FileInfo(file);

                        var movie = new Movie
                        {
                            OriginalFilePath = file,
                            FileName = fileInfo.Name,
                            DirectoryPath = fileInfo.DirectoryName,
                            FileSizeBytes = fileInfo.Length,
                            Status = "Discovered"
                        };

                        _parser.Parse(movie);
                        movies.Add(movie);

                        _progress.CurrentFile = fileInfo.Name;
                        _progress.Processed++;
                    }

                    SetCachedMovies(movies);
                }
                finally
                {
                    ResetProgress(isRunning: false);
                }
            });

            return new JsonResult(new { started = true });
        }

        public async Task<IActionResult> OnPostEnrichAsync()
        {
            if (!TryGetCachedMovies(out var movies))
                return RedirectToPage();

            var profile = _profileStore.GetActiveProfile();
            var sourceRoot = _config["PIM:ScanPath"] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(profile.DestinationRoot))
            {
                TempData["Message"] =
                    $"Destination profile '{profile.Name}' needs a destination root folder.";
                return RedirectToPage();
            }

            // Every newly scanned movie is enriched, including files that already
            // contain an IMDb ID. Rating and genre are required by organization
            // profiles and are not present in the filename alone.
            var moviesToEnrich = movies
                .Where(movie => !movie.MetadataFetched)
                .ToList();

            if (moviesToEnrich.Count > 0)
                await EnrichMoviesAsync(moviesToEnrich);

            _conflictDetection.ClearConflictState(movies);
            _duplicates.Process(movies);
            _rename.GeneratePreview(movies, profile, sourceRoot);
            _conflictDetection.ApplyConflictDetection(
                movies,
                profile.DestinationRoot);

            SetCachedMovies(movies);
            _cache.Remove(DryRunPreviewCacheKey);

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        public IActionResult OnPostCommit()
        {
            if (!TryGetCachedMovies(out var movies))
            {
                TempData["Message"] = "No movies are available to process.";
                return RedirectToPage();
            }

            var profile = _profileStore.GetActiveProfile();
            var sourceRoot = _config["PIM:ScanPath"] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(profile.DestinationRoot))
            {
                TempData["Message"] =
                    $"Destination profile '{profile.Name}' needs a destination root folder.";
                return RedirectToPage();
            }

            var profileChangedSincePreview = movies.Any(movie =>
                !string.IsNullOrWhiteSpace(movie.TargetPath) &&
                (movie.DestinationProfileId != profile.Id ||
                 movie.DestinationProfileRevision != profile.Revision));

            // Always rebuild the exact plan from the active profile before
            // conflict detection. A live commit is blocked when the user changed
            // profiles after the previous preview so a fresh dry run is required.
            _conflictDetection.ClearConflictState(movies);
            _rename.GeneratePreview(movies, profile, sourceRoot);
            _conflictDetection.ApplyConflictDetection(
                movies,
                profile.DestinationRoot);
            SetCachedMovies(movies);

            if (profileChangedSincePreview && !DryRun)
            {
                _cache.Remove(DryRunPreviewCacheKey);
                TempData["Message"] =
                    "The destination profile changed after the previous preview. " +
                    "PIM rebuilt the paths, but no files were moved. Run a new dry run before committing live.";

                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            var approvedMovies = movies
                .Where(movie =>
                    movie.ApprovedForCommit &&
                    !movie.NeedsReview &&
                    !movie.HasError)
                .ToList();

            _rename.ExecuteChanges(
                approvedMovies,
                DryRun,
                profile.DestinationRoot);

            SetCachedMovies(movies);
            var summary = BuildCommitSummary(movies, approvedMovies);

            if (DryRun)
            {
                DryRunPreview = _dryRunPreviewService.BuildPreview(movies);
                _cache.Set(
                    DryRunPreviewCacheKey,
                    DryRunPreview,
                    TimeSpan.FromMinutes(CacheDurationMinutes));

                TempData["Message"] =
                    $"Dry Run Complete using '{profile.Name}': " +
                    $"{summary.MoveCount} files would be moved, " +
                    $"{summary.DuplicateSkipCount} duplicates would be skipped, " +
                    $"{summary.ReviewCount} need review, " +
                    $"{summary.ErrorCount} errors found.";
            }
            else
            {
                _cache.Remove(DryRunPreviewCacheKey);

                TempData["Message"] =
                    $"Changes Applied using '{profile.Name}': " +
                    $"{summary.MoveCount} files processed, " +
                    $"{summary.DuplicateSkipCount} duplicates skipped, " +
                    $"{summary.ReviewCount} need review, " +
                    $"{summary.ErrorCount} errors found.";
            }

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        public IActionResult OnPostSaveSettings()
        {
            try
            {
                var appSettingsPath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "appsettings.json");

                var delayMs = _config.GetValue<int>(
                    "PIM:MetadataDelayMs",
                    DefaultMetadataDelayMs);

                var updatedSettings = new Dictionary<string, object?>
                {
                    ["Logging"] = new Dictionary<string, object?>
                    {
                        ["LogLevel"] = new Dictionary<string, string>
                        {
                            ["Default"] = "Information",
                            ["Microsoft.AspNetCore"] = "Warning"
                        }
                    },
                    ["PIM"] = new Dictionary<string, object?>
                    {
                        ["ScanPath"] = ScanPath,
                        ["OutputPath"] = OutputPath,
                        ["MetadataDelayMs"] = delayMs
                    },
                    ["AllowedHosts"] = "*"
                };

                var json = JsonSerializer.Serialize(
                    updatedSettings,
                    new JsonSerializerOptions { WriteIndented = true });

                System.IO.File.WriteAllText(appSettingsPath, json);

                var profile = _profileStore.GetActiveProfile();
                profile.DestinationRoot = OutputPath;
                _profileStore.Save(profile);

                _cache.Remove(DryRunPreviewCacheKey);
                TempData["Message"] =
                    "Library settings and the active destination profile were saved.";
            }
            catch (Exception ex)
            {
                TempData["Message"] = $"Error saving settings: {ex.Message}";
            }

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        private void LoadConfiguredPaths()
        {
            ActiveDestinationProfile = _profileStore.GetActiveProfile();
            ScanPath = _config["PIM:ScanPath"] ?? string.Empty;
            OutputPath = ActiveDestinationProfile.DestinationRoot;
        }

        private void LoadCachedMovies(bool showOnlyRecommended)
        {
            if (!TryGetCachedMovies(out var cachedMovies))
            {
                Movies = new List<Movie>();
                PreviewTree = null;
                return;
            }

            var planUsesDifferentProfile = cachedMovies.Any(movie =>
                !string.IsNullOrWhiteSpace(movie.TargetPath) &&
                (movie.DestinationProfileId != ActiveDestinationProfile.Id ||
                 movie.DestinationProfileRevision != ActiveDestinationProfile.Revision));

            if (planUsesDifferentProfile &&
                !string.IsNullOrWhiteSpace(ActiveDestinationProfile.DestinationRoot))
            {
                _conflictDetection.ClearConflictState(cachedMovies);
                _rename.GeneratePreview(
                    cachedMovies,
                    ActiveDestinationProfile,
                    ScanPath);
                _conflictDetection.ApplyConflictDetection(
                    cachedMovies,
                    ActiveDestinationProfile.DestinationRoot);

                SetCachedMovies(cachedMovies);
                _cache.Remove(DryRunPreviewCacheKey);
            }

            var sortedMovies = cachedMovies
                .OrderBy(movie => movie.Title)
                .ThenByDescending(movie => movie.KeepRecommended)
                .ThenByDescending(movie => movie.FileSizeBytes)
                .ToList();

            Movies = showOnlyRecommended
                ? sortedMovies
                    .Where(movie =>
                        !movie.IsDuplicate ||
                        movie.KeepRecommended ||
                        movie.IsAlternateVersion)
                    .ToList()
                : sortedMovies;

            BuildPreviewTree();
        }

        private void LoadCachedDryRunPreview()
        {
            if (_cache.TryGetValue(
                    DryRunPreviewCacheKey,
                    out DryRunPreviewResult? dryRunPreview))
            {
                DryRunPreview = dryRunPreview;
            }
        }

        private bool TryGetCachedMovies(out List<Movie> movies)
        {
            if (_cache.TryGetValue(
                    MovieScanCacheKey,
                    out List<Movie>? cachedMovies) &&
                cachedMovies != null)
            {
                movies = cachedMovies;
                return true;
            }

            movies = new List<Movie>();
            return false;
        }

        private void SetCachedMovies(List<Movie> movies)
        {
            _cache.Set(
                MovieScanCacheKey,
                movies,
                TimeSpan.FromMinutes(CacheDurationMinutes));
        }

        private async Task EnrichMoviesAsync(List<Movie> moviesToEnrich)
        {
            _progress.Total = moviesToEnrich.Count;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;
            _progress.IsRunning = true;

            try
            {
                var delayMs = _config.GetValue<int>(
                    "PIM:MetadataDelayMs",
                    DefaultMetadataDelayMs);

                foreach (var movie in moviesToEnrich)
                {
                    _progress.CurrentFile = movie.FileName ?? string.Empty;
                    await _metadata.EnrichAsync(movie);

                    if (delayMs > 0)
                        await Task.Delay(delayMs);

                    if (string.IsNullOrWhiteSpace(movie.ImdbId))
                    {
                        movie.NeedsReview = true;
                        movie.Status = "Metadata Not Found";
                    }
                    else if (!movie.NeedsReview)
                    {
                        movie.Status = "Metadata Enriched";
                    }

                    _progress.Processed++;
                }
            }
            finally
            {
                ResetProgress(isRunning: false);
            }
        }

        private void ResetProgress(bool isRunning = false)
        {
            _progress.Total = 0;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;
            _progress.IsRunning = isRunning;
        }

        private void BuildPreviewTree()
        {
            if (!string.IsNullOrWhiteSpace(ActiveDestinationProfile.DestinationRoot) &&
                Movies.Any(movie => !string.IsNullOrWhiteSpace(movie.TargetPath)))
            {
                PreviewTree = _treeService.BuildTree(
                    Movies,
                    ActiveDestinationProfile.DestinationRoot);
            }
            else
            {
                PreviewTree = null;
            }
        }

        private static CommitSummary BuildCommitSummary(
            List<Movie> allMovies,
            List<Movie> approvedMovies)
        {
            var moveCount = approvedMovies.Count;

            var duplicateSkipCount = allMovies.Count(movie =>
                !movie.NeedsReview &&
                !movie.HasError &&
                movie.IsDuplicate &&
                !movie.KeepRecommended &&
                !movie.IsAlternateVersion);

            var reviewCount = allMovies.Count(movie => movie.NeedsReview);
            var errorCount = allMovies.Count(movie => movie.HasError);

            return new CommitSummary(
                moveCount,
                duplicateSkipCount,
                reviewCount,
                errorCount);
        }

        private sealed record CommitSummary(
            int MoveCount,
            int DuplicateSkipCount,
            int ReviewCount,
            int ErrorCount);
    }
}
