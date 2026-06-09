using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Web.Models;
using PIM.Web.Services;
using System.Text.Json;

namespace PIM.Web.Pages
{
    /// <summary>
    /// Main dashboard page for Plex Integrity Manager (PIM).
    ///
    /// This page owns the current MVP workflow:
    /// 1. Scan the source library for movie files.
    /// 2. Parse filenames into preliminary movie records.
    /// 3. Enrich missing metadata through the configured metadata service.
    /// 4. Detect duplicates and alternate editions.
    /// 5. Generate the proposed Plex-friendly output structure.
    /// 6. Run a dry run or live commit.
    ///
    /// NOTE:
    /// Current state is cached in memory for the MVP. Later versions may move
    /// scan results, preview plans, and commit history into durable storage.
    /// </summary>
    public class IndexModel : PageModel
    {
        // =========================================================
        // Cache Keys / Defaults
        // =========================================================

        private const string MovieScanCacheKey = "MovieScan";
        private const string DryRunPreviewCacheKey = "DryRunPreview";
        private const int CacheDurationMinutes = 30;
        private const int DefaultMetadataDelayMs = 250;

        // =========================================================
        // Services
        // =========================================================

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

        // =========================================================
        // UI State
        // =========================================================

        /// <summary>
        /// When true, the results list is filtered to hide duplicate rows that
        /// are not recommended to be kept.
        /// </summary>
        [BindProperty(SupportsGet = true)]
        public bool ShowOnlyRecommended { get; set; }

        /// <summary>
        /// Source folder to scan for incoming movie files.
        /// Example: Y:\Transfer Movies
        /// </summary>
        [BindProperty]
        public string ScanPath { get; set; } = string.Empty;

        /// <summary>
        /// Destination folder where PIM will organize renamed movie files.
        /// Example: G:\[PLEX]\Movies
        /// </summary>
        [BindProperty]
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>
        /// When true, file operations are simulated only.
        /// This is the safest default for Alpha testing.
        /// </summary>
        [BindProperty]
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// Movies currently displayed in the results table.
        /// </summary>
        public List<Movie> Movies { get; set; } = new();

        /// <summary>
        /// Pseudo-folder tree showing the proposed Plex output structure.
        /// </summary>
        public FileNode? PreviewTree { get; set; }

        /// <summary>
        /// Dry-run summary and itemized preview shown after a dry-run commit.
        /// </summary>
        public DryRunPreviewResult? DryRunPreview { get; set; }

        // =========================================================
        // Constructor
        // =========================================================

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
            IDryRunPreviewService dryRunPreviewService)
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
        }

        // =========================================================
        // Page Load
        // =========================================================

        public Task OnGetAsync(bool showOnlyRecommended = false)
        {
            ShowOnlyRecommended = showOnlyRecommended;

            LoadConfiguredPaths();
            LoadCachedMovies(showOnlyRecommended);
            LoadCachedDryRunPreview();

            return Task.CompletedTask;
        }

        // =========================================================
        // Progress Endpoint
        // =========================================================

        /// <summary>
        /// Returns progress for long-running scan/enrichment operations.
        /// The UI polls this endpoint while background work is running.
        /// </summary>
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

        // =========================================================
        // Scan Library
        // =========================================================

        /// <summary>
        /// Discovers video files and parses filename clues.
        ///
        /// This step intentionally does not call OMDb or any external metadata
        /// provider. It should remain fast and filesystem-focused.
        /// </summary>
        public IActionResult OnPostRescan()
        {
            _cache.Remove(DryRunPreviewCacheKey);

            var rootPath = _config["PIM:ScanPath"] ?? string.Empty;

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

                        // Extract title/year guesses and version clues from the filename.
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

        // =========================================================
        // Enrich Metadata
        // =========================================================

        /// <summary>
        /// Enriches movies that are missing IMDb metadata, then rebuilds the
        /// duplicate decisions and proposed rename targets.
        /// </summary>
        public async Task<IActionResult> OnPostEnrichAsync()
        {
            if (!TryGetCachedMovies(out var movies))
            {
                return RedirectToPage();
            }

            var outputPath = _config["PIM:OutputPath"] ?? string.Empty;

            var moviesToEnrich = movies
                .Where(m => string.IsNullOrWhiteSpace(m.ImdbId))
                .ToList();

            if (moviesToEnrich.Count > 0)
            {
                await EnrichMoviesAsync(moviesToEnrich);
            }

            // Duplicate detection depends on IMDb IDs, so it runs after metadata enrichment.
            _duplicates.Process(movies);

            // Generate TargetPath values for the preview tree and commit workflow.
            _rename.GeneratePreview(movies, outputPath);

            SetCachedMovies(movies);
            _cache.Remove(DryRunPreviewCacheKey);

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        // =========================================================
        // Dry Run / Commit
        // =========================================================

        /// <summary>
        /// Executes the current plan.
        ///
        /// DryRun = true:
        /// - No files are moved, renamed, or deleted.
        /// - Statuses and preview rows are simulated for user validation.
        ///
        /// DryRun = false:
        /// - Approved keep/alternate files are moved and renamed.
        /// - Duplicate rows that are not kept are skipped/deleted according to
        ///   the rename service behavior.
        /// </summary>
        public IActionResult OnPostCommit()
        {
            if (!TryGetCachedMovies(out var movies))
            {
                TempData["Message"] = "No movies are available to process.";
                return RedirectToPage();
            }

            var approvedMovies = movies
                .Where(m =>
                    m.ApprovedForCommit &&
                    !m.NeedsReview &&
                    !m.HasError)
                .ToList();

            _rename.ExecuteChanges(approvedMovies, DryRun);

            SetCachedMovies(movies);

            // Use one shared summary calculation for both the banner and live commit message.
            // This prevents the top alert from drifting away from the dry-run cards.
            var summary = BuildCommitSummary(movies, approvedMovies);

            if (DryRun)
            {
                DryRunPreview = _dryRunPreviewService.BuildPreview(movies);
                _cache.Set(DryRunPreviewCacheKey, DryRunPreview, TimeSpan.FromMinutes(CacheDurationMinutes));

                TempData["Message"] =
                    $"Dry Run Complete: " +
                    $"{summary.MoveCount} files would be moved, " +
                    $"{summary.DuplicateSkipCount} duplicates would be skipped, " +
                    $"{summary.ReviewCount} need review, " +
                    $"{summary.ErrorCount} errors found.";
            }
            else
            {
                _cache.Remove(DryRunPreviewCacheKey);

                TempData["Message"] =
                    $"Changes Applied Successfully: " +
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

        // =========================================================
        // Save Library Settings
        // =========================================================

        /// <summary>
        /// Saves PIM library settings back to appsettings.json.
        ///
        /// MVP note:
        /// This keeps setup simple while the app is local-only. A later version
        /// may move settings to a user profile or database-backed configuration.
        /// </summary>
        public IActionResult OnPostSaveSettings()
        {
            try
            {
                var appSettingsPath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "appsettings.json");

                var delayMs = _config.GetValue<int>("PIM:MetadataDelayMs", DefaultMetadataDelayMs);

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

                TempData["Message"] = "Settings saved successfully.";
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

        // =========================================================
        // Private Helpers: Cache / Page State
        // =========================================================

        private void LoadConfiguredPaths()
        {
            ScanPath = _config["PIM:ScanPath"] ?? string.Empty;
            OutputPath = _config["PIM:OutputPath"] ?? string.Empty;
        }

        private void LoadCachedMovies(bool showOnlyRecommended)
        {
            if (!TryGetCachedMovies(out var cachedMovies))
            {
                Movies = new List<Movie>();
                PreviewTree = null;
                return;
            }

            var sortedMovies = cachedMovies
                .OrderBy(m => m.Title)
                .ThenByDescending(m => m.KeepRecommended)
                .ThenByDescending(m => m.FileSizeBytes)
                .ToList();

            Movies = showOnlyRecommended
                ? sortedMovies
                    .Where(m => !m.IsDuplicate || m.KeepRecommended || m.IsAlternateVersion)
                    .ToList()
                : sortedMovies;

            BuildPreviewTree();
        }

        private void LoadCachedDryRunPreview()
        {
            if (_cache.TryGetValue(DryRunPreviewCacheKey, out DryRunPreviewResult? dryRunPreview))
            {
                DryRunPreview = dryRunPreview;
            }
        }

        private bool TryGetCachedMovies(out List<Movie> movies)
        {
            if (_cache.TryGetValue(MovieScanCacheKey, out List<Movie>? cachedMovies)
                && cachedMovies != null)
            {
                movies = cachedMovies;
                return true;
            }

            movies = new List<Movie>();
            return false;
        }

        private void SetCachedMovies(List<Movie> movies)
        {
            _cache.Set(MovieScanCacheKey, movies, TimeSpan.FromMinutes(CacheDurationMinutes));
        }

        // =========================================================
        // Private Helpers: Metadata / Progress
        // =========================================================

        private async Task EnrichMoviesAsync(List<Movie> moviesToEnrich)
        {
            _progress.Total = moviesToEnrich.Count;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;
            _progress.IsRunning = true;

            try
            {
                var delayMs = _config.GetValue<int>("PIM:MetadataDelayMs", DefaultMetadataDelayMs);

                foreach (var movie in moviesToEnrich)
                {
                    _progress.CurrentFile = movie.FileName;

                    await _metadata.EnrichAsync(movie);

                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs);
                    }

                    if (string.IsNullOrWhiteSpace(movie.ImdbId))
                    {
                        movie.NeedsReview = true;
                        movie.Status = "Metadata Not Found";
                    }
                    else
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

        // =========================================================
        // Private Helpers: Preview / Summary
        // =========================================================

        /// <summary>
        /// Builds the pseudo-folder tree for the proposed Plex output structure.
        /// Uses TargetPath values generated by IRenameService.
        /// </summary>
        private void BuildPreviewTree()
        {
            var outputPath = _config["PIM:OutputPath"] ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(outputPath) &&
                Movies.Any(m => !string.IsNullOrWhiteSpace(m.TargetPath)))
            {
                PreviewTree = _treeService.BuildTree(Movies, outputPath);
            }
            else
            {
                PreviewTree = null;
            }
        }

        /// <summary>
        /// Calculates the banner summary used after dry-run or live commit.
        ///
        /// Important edition rule:
        /// Alternate editions are valid keep candidates. They should count as
        /// move/rename items when approved, not as duplicates to discard.
        ///
        /// Important duplicate rule:
        /// The duplicate skip count intentionally uses IsDuplicate + !KeepRecommended
        /// so it matches the current dry-run preview card behavior.
        /// </summary>
        private static CommitSummary BuildCommitSummary(
    List<Movie> allMovies,
    List<Movie> approvedMovies)
        {
            var duplicateSkipCount = allMovies.Count(m =>
                !m.NeedsReview &&
                !m.HasError &&
                m.IsDuplicate &&
                !m.KeepRecommended);

            var reviewCount = allMovies.Count(m => m.NeedsReview);
            var errorCount = allMovies.Count(m => m.HasError);

            var moveCount = allMovies.Count(m =>
                !m.NeedsReview &&
                !m.HasError &&
                !(m.IsDuplicate && !m.KeepRecommended));

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
