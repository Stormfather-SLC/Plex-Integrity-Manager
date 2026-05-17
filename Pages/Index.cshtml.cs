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
    /// Main dashboard page for Plex Intake Manager (PIM).
    ///
    /// RESPONSIBILITIES:
    /// - Display processed movie results from cache
    /// - Trigger file scanning
    /// - Trigger metadata enrichment
    /// - Provide real-time progress updates
    /// - Build the preview file tree
    /// - Handle dry run / commit actions
    /// </summary>
    public class IndexModel : PageModel
    {
        // =========================================================
        // 🔧 Services / Dependencies
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

        // =========================================================
        // 🎛️ UI State
        // =========================================================

        [BindProperty(SupportsGet = true)]
        public bool ShowOnlyRecommended { get; set; }

        // =========================================================
        // 📁 Library Settings
        // =========================================================

        /// <summary>
        /// Source folder to scan for movie files.
        /// Example: Y:\Transfer Movies
        /// </summary>
        [BindProperty]
        public string ScanPath { get; set; } = string.Empty;

        /// <summary>
        /// Destination folder where organized movies will be written.
        /// Example: G:\[PLEX]\Movies
        /// </summary>
        [BindProperty]
        public string OutputPath { get; set; } = string.Empty;
        /// <summary>
        /// Message displayed after saving settings.
        /// </summary>
        public string? SettingsMessage { get; set; }

        /// <summary>
        /// When true, PIM simulates all file operations without making
        /// any changes to disk.
        /// </summary>
        [BindProperty]
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// Movies displayed in the UI.
        /// </summary>
        public List<Movie> Movies { get; set; } = new();

        /// <summary>
        /// Preview tree showing the proposed output structure.
        /// </summary>
        public FileNode? PreviewTree { get; set; }

        // =========================================================
        // 🏗️ Constructor
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
            PreviewTreeService treeService)
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
        }

        // =========================================================
        // 📄 Page Load (GET)
        // =========================================================

        public Task OnGetAsync(bool showOnlyRecommended = false)
        {
            ShowOnlyRecommended = showOnlyRecommended;

            // Load configurable library paths from appsettings.json.
            ScanPath = _config["PIM:ScanPath"] ?? string.Empty;
            OutputPath = _config["PIM:OutputPath"] ?? string.Empty;

            if (_cache.TryGetValue("MovieScan", out List<Movie>? cachedMovies)
                && cachedMovies != null)
            {
                var sorted = cachedMovies
                    .OrderBy(m => m.Title)
                    .ThenByDescending(m => m.KeepRecommended)
                    .ThenByDescending(m => m.FileSizeBytes)
                    .ToList();

                Movies = showOnlyRecommended
                    ? sorted
                        .Where(m => !m.IsDuplicate || m.KeepRecommended)
                        .ToList()
                    : sorted;

                BuildPreviewTree();
            }
            else
            {
                Movies = new List<Movie>();
                PreviewTree = null;
            }

            return Task.CompletedTask;
        }

        // =========================================================
        // 📊 Progress Endpoint
        // =========================================================

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
        // 🔄 Scan Library
        // =========================================================

        /// <summary>
        /// Performs file discovery and filename parsing.
        /// This step does NOT call OMDb.
        /// </summary>
        public IActionResult OnPostRescan()
        {
            var rootPath = _config["PIM:ScanPath"];

            _progress.Total = 0;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;
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

                        // Parse title and year from filename.
                        _parser.Parse(movie);

                        movies.Add(movie);

                        // Update progress.
                        _progress.CurrentFile = fileInfo.Name;
                        _progress.Processed++;
                    }

                    // Save parsed results to cache.
                    _cache.Set("MovieScan", movies, TimeSpan.FromMinutes(30));
                }
                finally
                {
                    _progress.CurrentFile = string.Empty;
                    _progress.IsRunning = false;
                }
            });

            return new JsonResult(new { started = true });
        }

        // =========================================================
        // 🌐 Enrich Metadata
        // =========================================================

        /// <summary>
        /// Calls OMDb for movies missing an IMDb ID.
        /// Then runs duplicate detection and rename preview.
        /// </summary>
        public async Task<IActionResult> OnPostEnrichAsync()
        {
            if (!_cache.TryGetValue("MovieScan", out List<Movie>? movies)
                || movies == null)
            {
                return RedirectToPage();
            }

            var outputPath = _config["PIM:OutputPath"];

            // Only enrich movies that do not already have an IMDb ID.
            var moviesToEnrich = movies
                .Where(m => string.IsNullOrWhiteSpace(m.ImdbId))
                .ToList();

            if (moviesToEnrich.Count > 0)
            {
                _progress.Total = moviesToEnrich.Count;
                _progress.Processed = 0;
                _progress.CurrentFile = string.Empty;
                _progress.IsRunning = true;

                try
                {
                    // Read the delay from appsettings.json.
                    // Default to 250 ms if the setting is missing.
                    var delayMs = _config.GetValue<int>("PIM:MetadataDelayMs", 250);

                    foreach (var movie in moviesToEnrich)
                    {
                        // Show the original filename in the progress display.
                        _progress.CurrentFile = movie.FileName;

                        // Retrieve metadata from OMDb.
                        await _metadata.EnrichAsync(movie);

                        // Pause briefly between API calls.
                        // This helps avoid rate limiting and makes progress visible.
                        if (delayMs > 0)
                        {
                            await Task.Delay(delayMs);
                        }

                        // If metadata lookup failed, flag the movie for review.
                        if (string.IsNullOrWhiteSpace(movie.ImdbId))
                        {
                            movie.NeedsReview = true;
                            movie.Status = "Metadata Not Found";
                        }
                        else
                        {
                            movie.Status = "Metadata Enriched";
                        }

                        // Increment progress after processing the current movie.
                        _progress.Processed++;
                    }
                }
                finally
                {
                    _progress.CurrentFile = string.Empty;
                    _progress.IsRunning = false;
                }
            }

            // Detect duplicates now that metadata is available.
            _duplicates.Process(movies);

            // Generate proposed target paths.
            _rename.GeneratePreview(movies, outputPath);

            // Save updated results to cache.
            _cache.Set("MovieScan", movies, TimeSpan.FromMinutes(30));

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        // =========================================================
        // 🚀 Commit Changes
        // =========================================================

        /// <summary>
        /// Executes the planned file operations.
        ///
        /// If DryRun = true:
        /// - No files are modified.
        /// - Operations are simulated only.
        ///
        /// If DryRun = false:
        /// - Files are moved, renamed, and duplicates deleted.
        /// </summary>
        public IActionResult OnPostCommit()
        {
            if (!_cache.TryGetValue("MovieScan", out List<Movie>? movies)
                || movies == null)
            {
                TempData["Message"] = "No movies are available to process.";
                return RedirectToPage();
            }

            // Process only movies approved for commit.
            var approvedMovies = movies
                .Where(m => m.ApprovedForCommit)
                .ToList();

            // Execute the file operations.
            _rename.ExecuteChanges(approvedMovies, DryRun);
            var moveCount = approvedMovies.Count(m => !m.IsDuplicate || m.KeepRecommended);
            var duplicateDeleteCount = movies.Count(m =>
                m.IsDuplicate &&
                !m.KeepRecommended &&
                !m.IsAlternateVersion);

            var reviewCount = movies.Count(m => m.NeedsReview);
            var errorCount = movies.Count(m => m.HasError);

            // Save updated statuses back to cache.
            _cache.Set("MovieScan", movies, TimeSpan.FromMinutes(30));

            if (DryRun)
            {
                TempData["Message"] =
                    $"Dry Run Complete: " +
                    $"{moveCount} files would be moved, " +
                    $"{duplicateDeleteCount} duplicates would be skipped/deleted, " +
                    $"{reviewCount} need review, " +
                    $"{errorCount} errors found.";
            }
            else
            {
                TempData["Message"] =
                    $"Changes Applied Successfully: " +
                    $"{moveCount} files processed, " +
                    $"{reviewCount} need review, " +
                    $"{errorCount} errors found.";
            }

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        // =========================================================
        // 🌳 Build Preview Tree
        // =========================================================

        /// <summary>
        /// Builds the preview tree that shows the proposed folder and
        /// file structure after rename and organization.
        /// </summary>
        private void BuildPreviewTree()
        {
            var outputPath = _config["PIM:OutputPath"];

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
        // =========================================================
        // 💾 Save Library Settings
        // =========================================================

        // =========================================================
        // 💾 Save Library Settings
        // =========================================================

        /// <summary>
        /// Saves ScanPath and OutputPath to appsettings.json.
        /// </summary>
        public IActionResult OnPostSaveSettings()
        {
            try
            {
                var appSettingsPath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "appsettings.json");

                var delayMs = _config.GetValue<int>("PIM:MetadataDelayMs", 250);

                var updatedJson = $$"""
                {
                    "Logging": {
                    "LogLevel": {
                        "Default": "Information",
                        "Microsoft.AspNetCore": "Warning"
                    }
                    },
                    "PIM": {
                    "ScanPath": {{JsonSerializer.Serialize(ScanPath)}},
                    "OutputPath": {{JsonSerializer.Serialize(OutputPath)}},
                    "MetadataDelayMs": {{delayMs}}
                    },
                    "AllowedHosts": "*"
                }
                """;

                System.IO.File.WriteAllText(appSettingsPath, updatedJson);

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
    }
}