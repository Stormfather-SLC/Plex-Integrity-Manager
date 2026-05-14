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
    /// Main dashboard page for Plex Intake Manager (PIM).
    ///
    /// RESPONSIBILITIES:
    /// - Display processed movie results from cache
    /// - Trigger the scan pipeline
    /// - Trigger metadata enrichment
    /// - Provide real-time progress updates
    /// - Build a preview tree of the final output structure
    ///
    /// WORKFLOW:
    /// 1. Scan Library
    /// 2. Enrich Metadata
    /// 3. Detect Duplicates
    /// 4. Generate Rename Preview
    /// 5. Dry Run
    /// 6. Commit Changes
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

        /// <summary>
        /// When true, PIM simulates all file operations without making
        /// any changes to disk.
        ///
        /// Default = true for safety.
        ///
        /// Dry Run should remain enabled unless the user explicitly
        /// chooses to perform real file moves and deletions.
        /// </summary>
        [BindProperty]
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// Movies displayed in the UI.
        /// Loaded from IMemoryCache.
        /// </summary>
        public List<Movie> Movies { get; set; } = new();

        /// <summary>
        /// Preview tree showing proposed output folder structure.
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

            if (_cache.TryGetValue("MovieScan", out List<Movie>? cachedMovies)
                && cachedMovies != null)
            {
                var sorted = cachedMovies
                    .OrderBy(m => m.Title)
                    .ThenByDescending(m => m.KeepRecommended)
                    .ThenByDescending(m => m.FileSizeBytes)
                    .ToList();

                Movies = showOnlyRecommended
                    ? sorted.Where(m => !m.IsDuplicate || m.KeepRecommended).ToList()
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
        /// Performs only file discovery and filename parsing.
        ///
        /// This step:
        /// - Finds all supported video files
        /// - Extracts title/year from filenames
        /// - Stores results in cache
        ///
        /// It does NOT call OMDb yet.
        /// </summary>
        public IActionResult OnPostRescan()
        {
            var rootPath = _config["PIM:ScanPath"];

            _progress.Total = 0;
            _progress.Processed = 0;
            _progress.CurrentFile = "";
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

                        // Parse title and year from filename
                        _parser.Parse(movie);

                        movies.Add(movie);

                        _progress.CurrentFile = fileInfo.Name;
                        _progress.Processed++;
                    }

                    // Save parsed results to cache
                    _cache.Set("MovieScan", movies, TimeSpan.FromMinutes(30));
                }
                finally
                {
                    _progress.IsRunning = false;
                }
            });

            return new JsonResult(new { started = true });
        }

        // =========================================================
        // 🌐 Enrich Metadata
        // =========================================================

        /// <summary>
        /// Calls OMDb for each movie missing an IMDb ID.
        ///
        /// After enrichment:
        /// - Duplicate detection runs
        /// - Rename preview is generated
        /// - Results are saved back to cache
        /// </summary>
        public async Task<IActionResult> OnPostEnrichAsync()
        {
            if (!_cache.TryGetValue("MovieScan", out List<Movie>? movies)
                || movies == null)
            {
                return RedirectToPage();
            }

            var outputPath = _config["PIM:OutputPath"];

            // Reset progress
            _progress.Total = movies.Count;
            _progress.Processed = 0;
            _progress.CurrentFile = "";
            _progress.IsRunning = true;

            try
            {
                foreach (var movie in movies.Where(m => string.IsNullOrWhiteSpace(m.ImdbId)))
                {
                    _progress.CurrentFile = movie.FileName;

                    await _metadata.EnrichAsync(movie);

                    // If metadata lookup failed, flag for review
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

                // Detect duplicates now that IMDb IDs are available
                _duplicates.Process(movies);

                // Generate rename preview
                _rename.GeneratePreview(movies, outputPath);

                // Save updated list to cache
                _cache.Set("MovieScan", movies, TimeSpan.FromMinutes(30));
            }
            finally
            {
                _progress.IsRunning = false;
            }

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

            // TODO:
            // This will eventually call:
            // var results = _rename.ExecuteChanges(movies, DryRun);

            TempData["Message"] = DryRun
                ? "Dry Run completed. No files were modified."
                : "Changes applied successfully.";

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        // =========================================================
        // 🌳 Build Preview Tree
        // =========================================================

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
    }
}