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

        /// <summary>
        /// Dry Run Preview lines: moves first, then reviews, errors, and skips.
        /// Display only.
        /// </summary>
        public IReadOnlyList<DryRunPreviewRow> PreviewRows =>
            (DryRunPreview?.Items ?? new List<DryRunPreviewItem>())
                .Select(item => DryRunPreviewRow.Create(
                    item,
                    ScanPath,
                    ActiveDestinationProfile.DestinationRoot))
                .OrderBy(row => row.Category switch
                {
                    "move" => 0,
                    "review" => 1,
                    "error" => 2,
                    _ => 3
                })
                .ToList();

        /// <summary>
        /// Movie Results rows, things needing the owner first. Display only.
        /// </summary>
        public IReadOnlyList<MovieResultRow> ResultRows =>
            Movies
                .Select(movie => MovieResultRow.Create(
                    movie,
                    ScanPath,
                    ActiveDestinationProfile.DestinationRoot))
                .OrderBy(row => row.SortOrder)
                .ToList();

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

            TryGetCachedMovies(out var allMovies);
            LiveCommit = LiveCommitState.Evaluate(
                allMovies,
                _workflowState.GetDryRunApproval(),
                ActiveDestinationProfile,
                LibraryGoal,
                DateTime.UtcNow);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Whether the live-commit button is enabled, with the reason shown
        /// beneath the buttons.
        /// </summary>
        public LiveCommitState LiveCommit { get; private set; } =
            new(false, "Run a dry run first.", 0, null, null);

        public JsonResult OnGetProgress()
        {
            return new JsonResult(new
            {
                total = _progress.Total,
                processed = _progress.Processed,
                currentFile = _progress.CurrentFile,
                isRunning = _progress.IsRunning,
                canCancel = _progress.CanCancel,
                cancelRequested = _progress.CancelRequested,
                step = _progress.Step,
                busy = _progress.IsBusy,
                busyWith = _progress.BusyWith
            });
        }

        // PIM does one thing at a time. Every handler that reads and rewrites
        // the scanned movie list, the plan, or the settings claims this gate
        // first; a second action is refused, changing nothing, until the first
        // ends. The page also disables its controls, but that alone cannot
        // stop a second browser tab or a page reloaded part-way through.
        private const string ScanAction = "Scan Movies";
        private const string IdentifyAction = "Identify Movies";
        private const string DryRunAction = "a dry run";
        private const string LiveCommitAction = "a live commit";
        private const string DecisionAction = "a review decision";
        private const string SettingsAction = "a settings change";
        private const string PlanRefreshAction = "a plan update";

        /// <summary>
        /// The action in progress when this page was requested, or null. The
        /// page uses it to start out locked when it is opened mid-action.
        /// </summary>
        public string? BusyWith => _progress.BusyWith;

        private string BusyMessage() =>
            $"PIM is busy with {_progress.BusyWith ?? "another action"}. " +
            "Nothing was changed. Wait for it to finish, then try again.";

        private IActionResult RefuseBecauseBusy(string refusedAction)
        {
            _logger.LogWarning(
                "Refused {RefusedAction} because PIM is busy with {BusyWith}.",
                refusedAction,
                _progress.BusyWith ?? "another action");

            TempData["Message"] = BusyMessage();

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        private IActionResult RunExclusive(
            string action,
            Func<IActionResult> work)
        {
            if (!_progress.TryBeginAction(action))
                return RefuseBecauseBusy(action);

            try
            {
                return work();
            }
            finally
            {
                _progress.EndAction();
            }
        }

        private async Task<IActionResult> RunExclusiveAsync(
            string action,
            Func<Task<IActionResult>> work)
        {
            if (!_progress.TryBeginAction(action))
                return RefuseBecauseBusy(action);

            try
            {
                return await work();
            }
            finally
            {
                _progress.EndAction();
            }
        }

        public IActionResult OnPostRescan()
        {
            if (!_progress.TryBeginAction(ScanAction))
            {
                _logger.LogWarning(
                    "Refused {RefusedAction} because PIM is busy with {BusyWith}.",
                    ScanAction,
                    _progress.BusyWith ?? "another action");

                return new JsonResult(new
                {
                    started = false,
                    message = BusyMessage()
                });
            }

            // The scan runs in the background and releases the gate itself.
            var handedToBackgroundScan = false;

            try
            {
                var result = StartScan();
                handedToBackgroundScan = result.Started;

                return new JsonResult(new
                {
                    started = result.Started,
                    message = result.Message
                });
            }
            finally
            {
                if (!handedToBackgroundScan)
                    _progress.EndAction();
            }
        }

        private (bool Started, string? Message) StartScan()
        {
            InvalidateDryRunApproval();

            var rootPath = _config["PIM:ScanPath"] ?? string.Empty;
            var destinationRoot = _profileStore
                .GetActiveProfile()
                .DestinationRoot;

            if (string.IsNullOrWhiteSpace(rootPath))
                return (false, "A source folder must be configured before scanning.");

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
                    _progress.EndAction();
                }
            });

            return (true, null);
        }

        public Task<IActionResult> OnPostEnrichAsync() =>
            RunExclusiveAsync(IdentifyAction, IdentifyMoviesAsync);

        private async Task<IActionResult> IdentifyMoviesAsync()
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
            var cancellation = _progress.BeginCancellableOperation();
            var lookup = new LookupOutcome(0, 0, false);

            try
            {
                // Reuse completed metadata: only movies that are not settled yet
                // are looked up.
                var moviesToEnrich = GetMoviesToIdentify(movies);
                var stepCount = moviesToEnrich.Count > 0 ? 2 : 1;

                if (moviesToEnrich.Count > 0)
                {
                    BeginStep(1, stepCount, $"Looking up {CountOf(moviesToEnrich.Count, "movie")}");
                    lookup = await EnrichMoviesAsync(moviesToEnrich, cancellation);
                }

                // Cancel applies to the lookups only. Completed lookups are always
                // planned and saved below so stopping never loses finished work.
                _progress.EndCancellableOperation();

                if (lookup.Cancelled)
                {
                    _logger.LogInformation(
                        "Movie identification stopped by the user after {Completed} of {Total} lookup(s).",
                        lookup.Completed,
                        lookup.Total);
                }

                BeginStep(
                    stepCount,
                    stepCount,
                    "Checking duplicates, the destination folder and Plex");
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
                _progress.EndCancellableOperation();
                ResetProgress(isRunning: false);
            }

            if (lookup.Cancelled)
            {
                TempData["Message"] =
                    $"Identify Movies was stopped after {lookup.Completed} of {lookup.Total} lookup(s). " +
                    "Finished lookups were saved; movies not looked up yet show as Not identified. " +
                    "Run Identify Movies again to continue.";
            }

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        /// <summary>
        /// Asks a running Identify (or the lookup step of a dry run) to stop
        /// after the movie currently being looked up.
        /// </summary>
        public JsonResult OnPostCancel()
        {
            var requested = _progress.RequestCancel();

            if (requested)
                _logger.LogInformation("User asked to stop the running movie lookups.");

            return new JsonResult(new { requested });
        }

        public Task<IActionResult> OnPostAcceptSuggestedMatchAsync(Guid movieId) =>
            RunExclusiveAsync(DecisionAction, () => AcceptSuggestedMatchAsync(movieId));

        private async Task<IActionResult> AcceptSuggestedMatchAsync(Guid movieId)
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

        public Task<IActionResult> OnPostSetImdbIdAsync(Guid movieId, string? imdbId) =>
            RunExclusiveAsync(DecisionAction, () => SetImdbIdAsync(movieId, imdbId));

        private async Task<IActionResult> SetImdbIdAsync(Guid movieId, string? imdbId)
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

        public IActionResult OnPostAddPlexDuplicate(Guid movieId)
        {
            return RunExclusive(
                DecisionAction,
                () => ApplyPlexDuplicateDecision(movieId, addAnyway: true));
        }

        public IActionResult OnPostUndoPlexDuplicate(Guid movieId)
        {
            return RunExclusive(
                DecisionAction,
                () => ApplyPlexDuplicateDecision(movieId, addAnyway: false));
        }

        private IActionResult ApplyPlexDuplicateDecision(Guid movieId, bool addAnyway)
        {
            InvalidateDryRunApproval();

            if (!TryGetCachedMovies(out var movies))
            {
                TempData["Message"] = "The scan is no longer available. Scan the source folder again.";
                return RedirectToPage();
            }

            var movie = movies.SingleOrDefault(candidate => candidate.Id == movieId);
            var eligible = movie != null &&
                           (addAnyway
                               ? movie.NeedsPlexDuplicateDecision &&
                                 !string.IsNullOrWhiteSpace(movie.ExistingPlexLibraryPath)
                               : !string.IsNullOrWhiteSpace(movie.PlexDuplicateAcceptedPath));

            if (!eligible)
            {
                TempData["Message"] = addAnyway
                    ? "That file is no longer waiting for a possible-duplicate decision. Nothing was changed."
                    : "That file has no possible-duplicate decision to undo. Nothing was changed.";
                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            _logger.LogInformation(
                addAnyway
                    ? "User chose to add {FileName} alongside the existing Plex copy at {PlexPath}; invalidating prior dry-run approval and rebuilding the plan."
                    : "User undid the decision to add {FileName} alongside the existing Plex copy at {PlexPath}; invalidating prior dry-run approval and rebuilding the plan.",
                movie!.FileName ?? "<unknown>",
                addAnyway ? movie.ExistingPlexLibraryPath : movie.PlexDuplicateAcceptedPath);

            movie.PlexDuplicateAcceptedPath = addAnyway
                ? movie.ExistingPlexLibraryPath
                : null;
            movie.ApprovedForCommit = false;

            _moviePlan.Rebuild(
                movies,
                _profileStore.GetActiveProfile(),
                _config["PIM:ScanPath"] ?? string.Empty,
                GetConfiguredLibraryGoal());
            SetCachedMovies(movies);

            TempData["Message"] = !addAnyway
                ? $"'{movie.FileName}' will be skipped again because Plex already has this movie. Run a new dry run before live commit."
                : movie.NeedsReview || movie.HasError
                    ? $"'{movie.FileName}' will be added alongside the Plex copy, but it remains blocked: {movie.ReviewReason ?? movie.ErrorMessage ?? movie.Status}."
                    : $"'{movie.FileName}' will be added alongside the existing Plex copy; Plex will then have both. Nothing is deleted. Run a new dry run before live commit.";

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        public IActionResult OnPostConfirmFileName(Guid movieId) =>
            RunExclusive(DecisionAction, () => ConfirmFileName(movieId));

        private IActionResult ConfirmFileName(Guid movieId)
        {
            InvalidateDryRunApproval();

            if (!TryGetCachedMovies(out var movies))
            {
                TempData["Message"] = "The scan is no longer available. Scan the source folder again.";
                return RedirectToPage();
            }

            var movie = movies.SingleOrDefault(candidate => candidate.Id == movieId);

            if (movie == null || !movie.HasSuspiciousFileNameReview)
            {
                TempData["Message"] =
                    "That file no longer needs its name confirmed. Nothing was changed.";
                return RedirectToPage(new
                {
                    showOnlyRecommended = ShowOnlyRecommended
                });
            }

            _logger.LogInformation(
                "User confirmed the unusually long file name of {FileName}; invalidating prior dry-run approval and rebuilding the plan.",
                movie.FileName ?? "<unknown>");

            movie.FileNameConfirmed = true;
            movie.ApprovedForCommit = false;

            _moviePlan.Rebuild(
                movies,
                _profileStore.GetActiveProfile(),
                _config["PIM:ScanPath"] ?? string.Empty,
                GetConfiguredLibraryGoal());
            SetCachedMovies(movies);

            TempData["Message"] = movie.NeedsReview || movie.HasError
                ? $"The file name of '{movie.FileName}' was confirmed, but it remains blocked: {movie.ReviewReason ?? movie.ErrorMessage ?? movie.Status}."
                : $"The file name of '{movie.FileName}' was confirmed. Run a new dry run before live commit.";

            return RedirectToPage(new
            {
                showOnlyRecommended = ShowOnlyRecommended
            });
        }

        public IActionResult OnPostKeepDuplicateCopy(Guid movieId) =>
            RunExclusive(DecisionAction, () => KeepDuplicateCopy(movieId));

        private IActionResult KeepDuplicateCopy(Guid movieId)
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

        public Task<IActionResult> OnPostCommitAsync() =>
            RunExclusiveAsync(
                DryRun ? DryRunAction : LiveCommitAction,
                CommitAsync);

        private async Task<IActionResult> CommitAsync()
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

            // A dry run looks up only movies no lookup has been attempted for. If
            // Identify Movies has run, this list is empty and no OMDb calls are
            // made: a lookup that failed or is waiting on the owner is not
            // repeated here, because a dry run reports the plan as it stands.
            // Live commit never refreshes metadata because it must use the exact plan
            // approved by the preceding dry run.
            var moviesToEnrich = DryRun
                ? GetMoviesNeverLookedUp(movies)
                : new List<Movie>();
            var stepCount = moviesToEnrich.Count > 0 ? 3 : 2;
            var step = 0;

            if (DryRun)
            {
                if (moviesToEnrich.Count > 0)
                {
                    LookupOutcome lookup;
                    BeginStep(
                        ++step,
                        stepCount,
                        $"Looking up {CountOf(moviesToEnrich.Count, "movie")} not looked up yet");
                    _progress.IsRunning = true;
                    var cancellation = _progress.BeginCancellableOperation();

                    try
                    {
                        lookup = await EnrichMoviesAsync(moviesToEnrich, cancellation);
                    }
                    finally
                    {
                        _progress.EndCancellableOperation();
                        ResetProgress(isRunning: false);
                    }

                    // A dry run stopped part-way must never authorise a live
                    // commit. Keep the finished lookups, but create no approval.
                    if (lookup.Cancelled)
                    {
                        _moviePlan.Rebuild(movies, profile, sourceRoot, libraryGoal);
                        SetCachedMovies(movies);
                        InvalidateDryRunApproval();

                        _logger.LogInformation(
                            "Dry run stopped by the user during movie lookups after {Completed} of {Total}; no dry-run approval was created.",
                            lookup.Completed,
                            lookup.Total);

                        TempData["Message"] =
                            $"The dry run was stopped after {lookup.Completed} of {lookup.Total} movie lookup(s). " +
                            "No files were changed and no dry-run approval was created. Finished lookups were saved; " +
                            "run the dry run again when you're ready.";

                        return RedirectToPage(new
                        {
                            showOnlyRecommended = ShowOnlyRecommended
                        });
                    }
                }
            }

            // Rebuild the exact current plan immediately before either a dry run
            // or a live commit. This catches profile edits and destination changes.
            BeginStep(
                ++step,
                stepCount,
                DryRun
                    ? "Checking duplicates, the destination folder and Plex"
                    : "Re-checking duplicates, the destination folder and Plex");
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

            BeginStep(
                ++step,
                stepCount,
                DryRun
                    ? $"Simulating {CountOf(approvedMovies.Count, "approved move")}"
                    : $"Moving {CountOf(approvedMovies.Count, "approved file")}");

            // Only approved movies can be acted on; the rest are passed purely
            // so the operation journal records what was not done and why.
            _rename.ExecuteChanges(
                approvedMovies,
                DryRun,
                profile.DestinationRoot,
                movies.Except(approvedMovies).ToList());

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

        public IActionResult OnPostSaveSettings() =>
            RunExclusive(SettingsAction, SaveSettings);

        private IActionResult SaveSettings()
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

            // Showing the page must not re-plan the list while another action
            // is working on it. That action's own result is shown when it ends;
            // until then a stale plan cannot match a dry-run approval.
            if (planInputsChanged &&
                !string.IsNullOrWhiteSpace(ActiveDestinationProfile.DestinationRoot) &&
                _progress.TryBeginAction(PlanRefreshAction))
            {
                try
                {
                    _moviePlan.Rebuild(
                        cachedMovies,
                        ActiveDestinationProfile,
                        ScanPath,
                        LibraryGoal);

                    SetCachedMovies(cachedMovies);
                    InvalidateDryRunApproval();
                }
                finally
                {
                    _progress.EndAction();
                }
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

        /// <summary>
        /// Movies that Identify Movies asks OMDb about: those never looked up,
        /// and those whose earlier lookup failed or is still waiting on the
        /// owner. Pressing the button is the explicit request to try again.
        ///
        /// An identified movie is not asked about again because its MPA rating
        /// or genre is blank, even when the destination profile sorts by one.
        /// OMDb's full record, rating and genre included, is captured whenever
        /// a movie is identified, so a blank value means OMDb has none and
        /// asking again cannot fill it in.
        /// </summary>
        private static List<Movie> GetMoviesToIdentify(IEnumerable<Movie> movies)
        {
            return movies
                .Where(movie =>
                    !movie.MetadataFetched ||
                    movie.HasMetadataReviewReason ||
                    string.IsNullOrWhiteSpace(movie.Title) ||
                    !movie.Year.HasValue ||
                    string.IsNullOrWhiteSpace(movie.ImdbId))
                .ToList();
        }

        /// <summary>
        /// Movies a dry run looks up itself: only those no lookup has been
        /// attempted for (the page's "Not identified" movies). Repeating a
        /// lookup that already failed or produced a suggestion would return
        /// the same result, and a temporary OMDb problem during the repeat
        /// could push a settled movie back into review.
        /// </summary>
        private static List<Movie> GetMoviesNeverLookedUp(IEnumerable<Movie> movies)
        {
            return movies
                .Where(MovieResultRow.IsNotLookedUp)
                .ToList();
        }

        /// <summary>
        /// Names the part of the running action that the progress numbers
        /// belong to. A single-part action shows the description alone.
        /// </summary>
        private void BeginStep(int number, int count, string description)
        {
            _progress.Total = 0;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;
            _progress.Step = count > 1
                ? $"Step {number} of {count}: {description}"
                : description;
        }

        private static string CountOf(int count, string noun) =>
            $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}";

        /// <summary>Result of a metadata lookup pass.</summary>
        private readonly record struct LookupOutcome(int Completed, int Total, bool Cancelled);

        /// <summary>
        /// Looks up each movie in turn. Cancellation is honoured only between
        /// movies, so a movie is never left half-identified: it was either
        /// looked up completely or not at all.
        /// </summary>
        private async Task<LookupOutcome> EnrichMoviesAsync(
            List<Movie> moviesToEnrich,
            CancellationToken cancellationToken)
        {
            _progress.Total = moviesToEnrich.Count;
            _progress.Processed = 0;
            _progress.CurrentFile = string.Empty;

            var delayMs = _config.GetValue<int>(
                "PIM:MetadataDelayMs",
                DefaultMetadataDelayMs);
            var completed = 0;

            foreach (var movie in moviesToEnrich)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new LookupOutcome(completed, moviesToEnrich.Count, true);

                _progress.CurrentFile = movie.FileName ?? string.Empty;
                await _metadata.EnrichAsync(movie);
                completed++;

                if (delayMs > 0)
                {
                    try
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Finish recording this movie's result below, then stop.
                    }
                }

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

            return new LookupOutcome(
                completed,
                moviesToEnrich.Count,
                cancellationToken.IsCancellationRequested && completed < moviesToEnrich.Count);
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
