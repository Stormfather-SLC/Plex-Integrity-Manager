using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
        private const int DefaultMetadataDelayMs = 250;

        private readonly IFileScanner _scanner;
        private readonly IFileNameParser _parser;
        private readonly IMetadataService _metadata;
        private readonly IRenameService _rename;
        private readonly IConfiguration _config;
        private readonly IWorkflowStateStore _workflowState;
        private readonly ScanProgress _progress;
        private readonly PreviewTreeService _treeService;
        private readonly IDryRunPreviewService _dryRunPreviewService;
        private readonly IDestinationProfileStore _profileStore;
        private readonly IMoviePlanService _moviePlan;
        private readonly ILogger<IndexModel> _logger;
        private readonly IMetadataSuggestionService _metadataSuggestion;
        private readonly IDuplicateService _duplicates;

        [BindProperty(SupportsGet = true)]
        public bool ShowOnlyRecommended { get; set; }

        [BindProperty]
        public string ScanPath { get; set; } = string.Empty;

        [BindProperty]
        public string OutputPath { get; set; } = string.Empty;

        [BindProperty]
        public bool DryRun { get; set; } = true;

        [BindProperty]
        public LibraryGoal LibraryGoal { get; set; } =
            LibraryGoal.OrganizeNewMovies;

        public List<Movie> Movies { get; set; } = new();

        public FileNode? PreviewTree { get; set; }

        public DryRunPreviewResult? DryRunPreview { get; set; }

        public DestinationProfile ActiveDestinationProfile { get; set; } = new();

        /// <summary>
        /// Required integrations that are not configured. Items that depend on
        /// them fail closed, so the page explains why nothing is approved.
        /// </summary>
        public IReadOnlyList<string> MissingSetupItems => GetMissingSetupItems(_config);

        public static IReadOnlyList<string> GetMissingSetupItems(IConfiguration config)
        {
            var missing = new List<string>();

            if (string.IsNullOrWhiteSpace(config["Omdb:ApiKey"]))
                missing.Add("OMDb API key (Omdb:ApiKey) — needed to identify movies.");

            // Plex:BaseUrl is optional (defaults to http://localhost:32400).
            // The token is only required while the Plex check is on; when the
            // owner explicitly turns it off, PlexCheckDisabled warns instead.
            if (PlexSettings.IsLibraryCheckEnabled(config[PlexSettings.EnabledKey]) &&
                string.IsNullOrWhiteSpace(config["Plex:Token"]))
            {
                missing.Add("Plex token (Plex:Token) — needed to check your existing library.");
            }

            return missing;
        }

        /// <summary>
        /// True only when Plex:Enabled is explicitly false. Moves are then
        /// approved without checking whether the movie is already in Plex, so
        /// the page must say so.
        /// </summary>
        public bool PlexCheckDisabled =>
            !PlexSettings.IsLibraryCheckEnabled(_config[PlexSettings.EnabledKey]);

        public IndexModel(
            IFileScanner scanner,
            IFileNameParser parser,
            IMetadataService metadata,
            IRenameService rename,
            IConfiguration config,
            IWorkflowStateStore workflowState,
            ScanProgress progress,
            PreviewTreeService treeService,
            IDryRunPreviewService dryRunPreviewService,
            IDestinationProfileStore profileStore,
            IMoviePlanService moviePlan,
            IMetadataSuggestionService metadataSuggestion,
            IDuplicateService duplicates,
            ILogger<IndexModel> logger)
        {
            _scanner = scanner;
            _parser = parser;
            _metadata = metadata;
            _rename = rename;
            _config = config;
            _workflowState = workflowState;
            _progress = progress;
            _treeService = treeService;
            _dryRunPreviewService = dryRunPreviewService;
            _profileStore = profileStore;
            _moviePlan = moviePlan;
            _metadataSuggestion = metadataSuggestion;
            _duplicates = duplicates;
            _logger = logger;
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
            var destinationRoot = _profileStore
                .GetActiveProfile()
                .DestinationRoot;

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

            _logger.LogInformation(
                "Movie scan started for the configured source with destination exclusion enabled.");

            _ = Task.Run(() =>
            {
                try
                {
                    var files = _scanner.GetFiles(
                        rootPath,
                        destinationRoot);
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

                    _workflowState.SaveMovies(movies, rootPath);
                    _logger.LogInformation(
                        "Movie scan completed with {MovieCount} discovered movie file(s).",
                        movies.Count);
                }
                catch (Exception ex)
                {
                    _progress.Message =
                        "Movie scan failed. Review the application log for details.";
                    _logger.LogError(ex, "Movie scan failed.");
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
            var libraryGoal = GetConfiguredLibraryGoal();

            _logger.LogInformation(
                "Movie identification started for {MovieCount} movie(s) using workflow {Workflow}.",
                movies.Count,
                LibraryGoalSettings.GetDisplayName(libraryGoal));

            if (string.IsNullOrWhiteSpace(profile.DestinationRoot))
            {
                TempData["Message"] =
                    $"Destination profile '{profile.Name}' needs a destination root folder.";
                return RedirectToPage();
            }

            ResetProgress();
            _progress.IsRunning = true;

            try
            {
                // Reuse completed metadata, but refresh stale cached records when the
                // active profile needs a field that was not captured previously.
                var moviesToEnrich = GetMoviesRequiringMetadata(movies, profile);

                if (moviesToEnrich.Count > 0)
                    await EnrichMoviesAsync(moviesToEnrich);

                _progress.CurrentFile = "Checking duplicates and destination conflicts...";

                _progress.CurrentFile = "Checking Plex library conflicts...";
                _moviePlan.Rebuild(
                    movies,
                    profile,
                    sourceRoot,
                    libraryGoal);

                _progress.CurrentFile = "Saving identification results...";
                SetCachedMovies(movies);
                InvalidateDryRunApproval();
                _logger.LogInformation(
                    "Movie identification and planning completed with {ReviewCount} review item(s) and {ErrorCount} error(s).",
                    movies.Count(movie => movie.NeedsReview),
                    movies.Count(movie => movie.HasError));
            }
            finally
            {
                // Keep the Identify operation marked running until metadata, duplicate
                // analysis, path generation, Plex validation, and the final cache write
                // have all completed. Otherwise the browser can reload stale scan state
                // while conflict detection is still running.
                ResetProgress(isRunning: false);
            }

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        public async Task<IActionResult> OnPostAcceptSuggestedMatchAsync(Guid movieId)
        {
            InvalidateDryRunApproval();

            if (!TryGetCachedMovies(out var movies))
            {
                TempData["Message"] = "The scan is no longer available. Scan the source folder again.";
                return RedirectToPage();
            }

            var movie = movies.SingleOrDefault(candidate => candidate.Id == movieId);

            if (movie == null || !movie.CanAcceptMetadataSuggestion)
            {
                TempData["Message"] =
                    "No concrete metadata suggestion is available for that movie.";
                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            var selectedTitle = movie.SuggestedTitle!;
            var selectedYear = movie.SuggestedYear!.Value;
            var selectedImdbId = movie.SuggestedImdbId!;

            _logger.LogInformation(
                "User accepted suggested metadata identity {Title} ({Year}), IMDb {ImdbId}, for {FileName}; invalidating prior dry-run approval and rebuilding the plan.",
                selectedTitle,
                selectedYear,
                selectedImdbId,
                movie.FileName ?? "<unknown>");

            var profile = _profileStore.GetActiveProfile();
            var sourceRoot = _config["PIM:ScanPath"] ?? string.Empty;
            var libraryGoal = GetConfiguredLibraryGoal();

            var accepted = await _metadataSuggestion.AcceptAsync(
                movie,
                movies,
                profile,
                sourceRoot,
                libraryGoal);

            if (!accepted)
            {
                TempData["Message"] =
                    "The metadata suggestion was no longer available. No identity was changed.";
                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            SetCachedMovies(movies);

            TempData["Message"] = movie.NeedsReview || movie.HasError
                ? $"Suggested match applied for '{movie.FileName}', but the item remains blocked: {movie.ReviewReason ?? movie.ErrorMessage ?? movie.Status}."
                : $"Suggested match applied for '{movie.FileName}'. The downstream plan was rebuilt; run a new dry run before live commit.";

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        public async Task<IActionResult> OnPostSetImdbIdAsync(Guid movieId, string? imdbId)
        {
            InvalidateDryRunApproval();

            if (!TryGetCachedMovies(out var movies))
            {
                TempData["Message"] = "The scan is no longer available. Scan the source folder again.";
                return RedirectToPage();
            }

            var movie = movies.SingleOrDefault(candidate => candidate.Id == movieId);

            if (!ImdbIdInput.TryNormalize(imdbId, out var normalizedImdbId))
            {
                TempData["Message"] =
                    "Enter one IMDb ID such as tt0088794, or paste the movie's IMDb page address. Nothing was changed.";
                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            if (movie == null || !movie.CanEnterImdbId)
            {
                TempData["Message"] =
                    "That movie no longer needs an IMDb ID. Nothing was changed.";
                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            _logger.LogInformation(
                "User supplied IMDb ID {ImdbId} for {FileName}; invalidating prior dry-run approval, validating the ID with OMDb, and rebuilding the plan.",
                normalizedImdbId,
                movie.FileName ?? "<unknown>");

            var applied = await _metadataSuggestion.ApplyImdbIdAsync(
                movie,
                normalizedImdbId,
                movies,
                _profileStore.GetActiveProfile(),
                _config["PIM:ScanPath"] ?? string.Empty,
                GetConfiguredLibraryGoal());

            if (!applied)
            {
                TempData["Message"] = "The IMDb ID could not be applied. Nothing was changed.";
                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            SetCachedMovies(movies);

            TempData["Message"] = movie.NeedsReview || movie.HasError
                ? $"{normalizedImdbId} was looked up for '{movie.FileName}', but the item still needs review: {movie.ReviewReason ?? movie.ErrorMessage ?? movie.Status}."
                : $"{normalizedImdbId} identified '{movie.FileName}' as {movie.Title} ({movie.Year}). Run a new dry run before live commit.";

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        public IActionResult OnPostKeepDuplicateCopy(Guid movieId)
        {
            InvalidateDryRunApproval();

            if (!TryGetCachedMovies(out var movies))
            {
                TempData["Message"] = "The scan is no longer available. Scan the source folder again.";
                return RedirectToPage();
            }

            var movie = movies.SingleOrDefault(candidate => candidate.Id == movieId);

            if (movie == null || !_duplicates.ChoosePreferredCopy(movie, movies))
            {
                TempData["Message"] =
                    "That file is no longer part of an unresolved duplicate choice. Nothing was changed.";
                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            _logger.LogInformation(
                "User chose {FileName} as the preferred copy of {Title} ({Year}), IMDb {ImdbId}; invalidating prior dry-run approval and rebuilding the plan.",
                movie.FileName ?? "<unknown>",
                movie.Title,
                movie.Year,
                movie.ImdbId);

            _moviePlan.Rebuild(
                movies,
                _profileStore.GetActiveProfile(),
                _config["PIM:ScanPath"] ?? string.Empty,
                GetConfiguredLibraryGoal());
            SetCachedMovies(movies);

            TempData["Message"] = movie.NeedsReview || movie.HasError
                ? $"'{movie.FileName}' was chosen as the copy to keep, but it remains blocked: {movie.ReviewReason ?? movie.ErrorMessage ?? movie.Status}."
                : $"'{movie.FileName}' was chosen as the copy to keep. The other copies will be skipped, not deleted. Run a new dry run before live commit.";

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
            var libraryGoal = GetConfiguredLibraryGoal();

            _logger.LogInformation(
                "{Operation} started for workflow {Workflow} and profile {ProfileName} revision {ProfileRevision}.",
                DryRun ? "Dry run" : "Live commit",
                LibraryGoalSettings.GetDisplayName(libraryGoal),
                profile.Name,
                profile.Revision);

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
                {
                    await EnrichMoviesAsync(moviesToEnrich);
                    ResetProgress(isRunning: false);
                }
            }

            // Rebuild the exact current plan immediately before either a dry run
            // or a live commit. This catches profile edits and destination changes.
            _moviePlan.Rebuild(
                movies,
                profile,
                sourceRoot,
                libraryGoal);
            SetCachedMovies(movies);

            var approvedMovies = movies
                .Where(movie =>
                    movie.ApprovedForCommit &&
                    !movie.NeedsReview &&
                    !movie.HasError)
                .ToList();

            var currentPlanFingerprint = PlanFingerprintBuilder.Build(
                movies,
                profile,
                libraryGoal);

            if (!DryRun && !HasMatchingDryRunApproval(
                    profile,
                    currentPlanFingerprint,
                    libraryGoal))
            {
                TempData["Message"] =
                    "No files were moved. The current destination plan does not have a matching dry-run approval. " +
                    "Run a new dry run, review the results, and then return to live commit.";

                _logger.LogWarning(
                    "Live commit rejected because the current plan did not match the approved dry run.");

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
                DryRunPreview = _dryRunPreviewService.BuildPreview(
                    movies,
                    libraryGoal);
                _workflowState.SaveDryRun(
                    DryRunPreview,
                    new DryRunApproval(
                        profile.Id,
                        profile.Revision,
                        libraryGoal,
                        currentPlanFingerprint,
                        DateTime.UtcNow));

                TempData["Message"] =
                    $"Dry Run Complete using '{profile.Name}': " +
                    $"Workflow: {LibraryGoalSettings.GetDisplayName(libraryGoal)}. " +
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
                    $"Workflow: {LibraryGoalSettings.GetDisplayName(libraryGoal)}. " +
                    $"{summary.MoveCount} files processed, " +
                    $"{summary.DuplicateSkipCount} duplicates skipped, " +
                    $"{summary.ReviewCount} need review, " +
                    $"{summary.ErrorCount} errors found." +
                    (string.IsNullOrWhiteSpace(_rename.LastJournalLocation)
                        ? string.Empty
                        : $" A record of every file operation was saved to {_rename.LastJournalLocation}.");
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
                var previousLibraryGoal = GetConfiguredLibraryGoal();
                var profile = _profileStore.GetActiveProfile();
                var previousOutputPath = profile.DestinationRoot;
                ScanPath = ScanPath.Trim();
                OutputPath = OutputPath.Trim();

                if (string.IsNullOrWhiteSpace(ScanPath))
                    throw new InvalidOperationException("A source folder is required.");

                if (string.IsNullOrWhiteSpace(OutputPath))
                    throw new InvalidOperationException("A destination folder is required.");

                if (!Enum.IsDefined(LibraryGoal))
                    throw new InvalidOperationException("A valid workflow is required.");

                var delayMs = _config.GetValue<int>(
                    "PIM:MetadataDelayMs",
                    DefaultMetadataDelayMs);

                var updatedSettings = new Dictionary<string, object?>
                {
                    ["PIM"] = new Dictionary<string, object?>
                    {
                        ["ScanPath"] = ScanPath,
                        ["OutputPath"] = OutputPath,
                        ["LibraryGoal"] = LibraryGoal.ToString(),
                        ["MetadataDelayMs"] = delayMs
                    }
                };

                var json = JsonSerializer.Serialize(
                    updatedSettings,
                    new JsonSerializerOptions { WriteIndented = true });

                var settingsDirectory = UserSettingsLocation.GetDirectory(_config);
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
                _config["PIM:LibraryGoal"] = LibraryGoal.ToString();

                profile.DestinationRoot = OutputPath;
                _profileStore.Save(profile);

                var settingsChange = WorkflowSettingsChange.Evaluate(
                    previousScanPath,
                    ScanPath,
                    previousOutputPath,
                    OutputPath,
                    previousLibraryGoal,
                    LibraryGoal);

                if (settingsChange.ClearScan)
                {
                    _workflowState.ClearMovies();
                    ResetProgress();
                }
                else if (settingsChange.RebuildExistingPlan &&
                         TryGetCachedMovies(out var cachedMovies))
                {
                    // Keep the scan and completed metadata. Rebuild only the
                    // duplicate/path/conflict policy affected by these settings.
                    _moviePlan.Rebuild(
                        cachedMovies,
                        profile,
                        ScanPath,
                        LibraryGoal);
                    SetCachedMovies(cachedMovies);
                }

                InvalidateDryRunApproval();
                TempData["Message"] = settingsChange.SourceChanged
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
            LibraryGoal = GetConfiguredLibraryGoal();
        }

        private void LoadCachedMovies(bool showOnlyRecommended)
        {
            if (!TryGetCachedMovies(out var cachedMovies))
            {
                Movies = new List<Movie>();
                PreviewTree = null;
                return;
            }

            var planInputsChanged = cachedMovies.Any(movie =>
                !string.IsNullOrWhiteSpace(movie.TargetPath) &&
                (movie.DestinationProfileId != ActiveDestinationProfile.Id ||
                 movie.DestinationProfileRevision != ActiveDestinationProfile.Revision ||
                 movie.PlannedLibraryGoal != LibraryGoal));

            if (planInputsChanged &&
                !string.IsNullOrWhiteSpace(ActiveDestinationProfile.DestinationRoot))
            {
                _moviePlan.Rebuild(
                    cachedMovies,
                    ActiveDestinationProfile,
                    ScanPath,
                    LibraryGoal);

                SetCachedMovies(cachedMovies);
                InvalidateDryRunApproval();
            }

            var sortedMovies = cachedMovies
                .OrderByDescending(movie => movie.HasError)
                .ThenByDescending(movie => movie.NeedsReview)
                .ThenBy(movie => movie.Title)
                .ThenByDescending(movie => movie.KeepRecommended)
                .ThenByDescending(movie => movie.FileSizeBytes)
                .ToList();

            Movies = showOnlyRecommended
                ? sortedMovies
                    .Where(movie =>
                        movie.NeedsReview ||
                        movie.HasError ||
                        !movie.IsDuplicate ||
                        movie.KeepRecommended ||
                        movie.IsAlternateVersion)
                    .ToList()
                : sortedMovies;

            BuildPreviewTree();
        }

        private void LoadCachedDryRunPreview()
        {
            DryRunPreview = _workflowState.GetDryRunPreview();
        }

        private bool TryGetCachedMovies(out List<Movie> movies)
        {
            return _workflowState.TryGetMovies(out movies);
        }

        private void SetCachedMovies(List<Movie> movies)
        {
            _workflowState.SaveMovies(
                movies,
                _config["PIM:ScanPath"] ?? string.Empty);
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
                    movie.HasMetadataReviewReason ||
                    string.IsNullOrWhiteSpace(movie.Title) ||
                    !movie.Year.HasValue ||
                    string.IsNullOrWhiteSpace(movie.ImdbId) ||
                    (profileUsesRating && string.IsNullOrWhiteSpace(movie.MpaRating)) ||
                    (profileUsesGenre && string.IsNullOrWhiteSpace(movie.PrimaryGenre)))
                .ToList();
        }

        private async Task EnrichMoviesAsync(List<Movie> moviesToEnrich)
        {
            _progress.Total = moviesToEnrich.Count;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;

            var delayMs = _config.GetValue<int>(
                "PIM:MetadataDelayMs",
                DefaultMetadataDelayMs);

            foreach (var movie in moviesToEnrich)
            {
                _progress.CurrentFile = movie.FileName ?? string.Empty;
                await _metadata.EnrichAsync(movie);

                if (delayMs > 0)
                    await Task.Delay(delayMs);

                if (string.IsNullOrWhiteSpace(movie.ImdbId) &&
                    !movie.HasMetadataReviewReason)
                {
                    movie.SetMetadataReview(
                        "IMDb ID could not be determined",
                        MetadataLookupFailureType.MissingRequiredFields,
                        "OMDb did not provide an IMDb ID.");
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

        private void ResetProgress(bool isRunning = false)
        {
            _progress.Total = 0;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;
            _progress.IsRunning = isRunning;
        }

        private void BuildPreviewTree()
        {
            PreviewTree = Movies.Any()
                ? _treeService.BuildTree(
                    Movies,
                    ActiveDestinationProfile.DestinationRoot)
                : null;
        }

        private bool HasMatchingDryRunApproval(
            DestinationProfile profile,
            string currentPlanFingerprint,
            LibraryGoal libraryGoal)
        {
            var approval = _workflowState.GetDryRunApproval();

            return approval != null &&
                   approval.Matches(
                       profile,
                       libraryGoal,
                       currentPlanFingerprint);
        }

        private void InvalidateDryRunApproval()
        {
            _workflowState.InvalidateDryRunApproval();
        }

        private LibraryGoal GetConfiguredLibraryGoal()
        {
            return LibraryGoalSettings.Parse(_config["PIM:LibraryGoal"]);
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
