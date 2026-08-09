using System.Security.Cryptography;
using System.Text;
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
        private const string DryRunApprovalCacheKey = "DryRunApproval";
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
            InvalidateDryRunApproval();

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

            // Reuse completed metadata, but refresh stale cached records when the
            // active profile needs a field that was not captured previously.
            var moviesToEnrich = GetMoviesRequiringMetadata(movies, profile);

            if (moviesToEnrich.Count > 0)
                await EnrichMoviesAsync(moviesToEnrich);

            _conflictDetection.ClearConflictState(movies);
            _duplicates.Process(movies);
            _rename.GeneratePreview(movies, profile, sourceRoot);
            _conflictDetection.ApplyConflictDetection(
                movies,
                profile.DestinationRoot);

            SetCachedMovies(movies);
            InvalidateDryRunApproval();

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        public async Task<IActionResult> OnPostCommitAsync()
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

            // A dry run may fill only metadata that is still missing. If Identify
            // Movies just completed, this list is empty and no OMDb calls are made.
            // Live commit never refreshes metadata because it must use the exact plan
            // approved by the preceding dry run.
            if (DryRun)
            {
                var moviesToEnrich = GetMoviesRequiringMetadata(movies, profile);

                if (moviesToEnrich.Count > 0)
                    await EnrichMoviesAsync(moviesToEnrich);
            }

            // Rebuild the exact current plan immediately before either a dry run
            // or a live commit. This catches profile edits and destination changes.
            _conflictDetection.ClearConflictState(movies);
            _duplicates.Process(movies);
            _rename.GeneratePreview(movies, profile, sourceRoot);
            _conflictDetection.ApplyConflictDetection(
                movies,
                profile.DestinationRoot);
            SetCachedMovies(movies);

            var approvedMovies = movies
                .Where(movie =>
                    movie.ApprovedForCommit &&
                    !movie.NeedsReview &&
                    !movie.HasError)
                .ToList();

            var currentPlanFingerprint = BuildPlanFingerprint(movies, profile);

            if (!DryRun && !HasMatchingDryRunApproval(
                    profile,
                    currentPlanFingerprint))
            {
                TempData["Message"] =
                    "No files were moved. The current destination plan does not have a matching dry-run approval. " +
                    "Run a new dry run, review the results, and then return to live commit.";

                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

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
                _cache.Set(
                    DryRunApprovalCacheKey,
                    new DryRunApproval(
                        profile.Id,
                        profile.Revision,
                        currentPlanFingerprint,
                        DateTime.UtcNow),
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
                InvalidateDryRunApproval();

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
            string? temporaryPath = null;

            try
            {
                var previousScanPath = _config["PIM:ScanPath"] ?? string.Empty;
                ScanPath = ScanPath.Trim();
                OutputPath = OutputPath.Trim();

                if (string.IsNullOrWhiteSpace(ScanPath))
                    throw new InvalidOperationException("A source folder is required.");

                if (string.IsNullOrWhiteSpace(OutputPath))
                    throw new InvalidOperationException("A destination folder is required.");

                var delayMs = _config.GetValue<int>(
                    "PIM:MetadataDelayMs",
                    DefaultMetadataDelayMs);

                var updatedSettings = new Dictionary<string, object?>
                {
                    ["PIM"] = new Dictionary<string, object?>
                    {
                        ["ScanPath"] = ScanPath,
                        ["OutputPath"] = OutputPath,
                        ["MetadataDelayMs"] = delayMs
                    }
                };

                var json = JsonSerializer.Serialize(
                    updatedSettings,
                    new JsonSerializerOptions { WriteIndented = true });

                var settingsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Plex Integrity Manager");
                Directory.CreateDirectory(settingsDirectory);

                var settingsPath = Path.Combine(
                    settingsDirectory,
                    "library-settings.json");
                temporaryPath = settingsPath + ".tmp";

                System.IO.File.WriteAllText(temporaryPath, json);
                System.IO.File.Move(
                    temporaryPath,
                    settingsPath,
                    overwrite: true);
                temporaryPath = null;

                // Make the new values available immediately. The JSON provider also
                // reloads them for future requests and future application launches.
                _config["PIM:ScanPath"] = ScanPath;
                _config["PIM:OutputPath"] = OutputPath;

                var profile = _profileStore.GetActiveProfile();
                profile.DestinationRoot = OutputPath;
                _profileStore.Save(profile);

                var sourceChanged = !string.Equals(
                    previousScanPath.Trim(),
                    ScanPath,
                    StringComparison.OrdinalIgnoreCase);

                if (sourceChanged)
                {
                    _cache.Remove(MovieScanCacheKey);
                    ResetProgress();
                }

                InvalidateDryRunApproval();
                TempData["Message"] = sourceChanged
                    ? "Library settings were saved. The previous scan was cleared because the source folder changed."
                    : "Library settings and the active destination profile were saved.";
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath) &&
                    System.IO.File.Exists(temporaryPath))
                {
                    try
                    {
                        System.IO.File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // Preserve the original settings error.
                    }
                }

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
                InvalidateDryRunApproval();
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

        private static List<Movie> GetMoviesRequiringMetadata(
            IEnumerable<Movie> movies,
            DestinationProfile profile)
        {
            var profileUsesRating = profile.OrganizationLevels.Any(level =>
                level.Type == OrganizationLevelType.MpaRating);
            var profileUsesGenre = profile.OrganizationLevels.Any(level =>
                level.Type == OrganizationLevelType.PrimaryGenre);

            return movies
                .Where(movie =>
                    !movie.MetadataFetched ||
                    (!string.IsNullOrWhiteSpace(movie.ImdbId) &&
                     (string.IsNullOrWhiteSpace(movie.Title) ||
                      !movie.Year.HasValue ||
                      (profileUsesRating && string.IsNullOrWhiteSpace(movie.MpaRating)) ||
                      (profileUsesGenre && string.IsNullOrWhiteSpace(movie.PrimaryGenre)))))
                .ToList();
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
                    else if (!movie.NeedsReview &&
                             !string.Equals(
                                 movie.Status,
                                 "IMDb ID Match",
                                 StringComparison.Ordinal))
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

        private bool HasMatchingDryRunApproval(
            DestinationProfile profile,
            string currentPlanFingerprint)
        {
            return _cache.TryGetValue(
                       DryRunApprovalCacheKey,
                       out DryRunApproval? approval) &&
                   approval != null &&
                   approval.ProfileId == profile.Id &&
                   approval.ProfileRevision == profile.Revision &&
                   string.Equals(
                       approval.PlanFingerprint,
                       currentPlanFingerprint,
                       StringComparison.Ordinal);
        }

        private void InvalidateDryRunApproval()
        {
            _cache.Remove(DryRunPreviewCacheKey);
            _cache.Remove(DryRunApprovalCacheKey);
        }

        private static string BuildPlanFingerprint(
            IEnumerable<Movie> movies,
            DestinationProfile profile)
        {
            var planLines = movies
                .OrderBy(movie => movie.Id)
                .Select(movie => string.Join(
                    '|',
                    movie.Id.ToString("N"),
                    movie.TargetPath ?? string.Empty,
                    movie.ApprovedForCommit,
                    movie.NeedsReview,
                    movie.HasError,
                    movie.HasDestinationConflict,
                    movie.HasPlexLibraryConflict));

            var payload = string.Join(
                Environment.NewLine,
                new[]
                {
                    profile.Id.ToString("N"),
                    profile.Revision.ToString(),
                    profile.DestinationRoot
                }.Concat(planLines));

            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
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

        private sealed record DryRunApproval(
            Guid ProfileId,
            int ProfileRevision,
            string PlanFingerprint,
            DateTime CreatedUtc);

        private sealed record CommitSummary(
            int MoveCount,
            int DuplicateSkipCount,
            int ReviewCount,
            int ErrorCount);
    }
}
