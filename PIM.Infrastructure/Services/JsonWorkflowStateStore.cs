using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services;

/// <summary>
/// Keeps workflow state in memory and mirrors it to JSON files under
/// %LOCALAPPDATA%\Plex Integrity Manager\Workflow (overridable with
/// PIM:WorkflowStateDirectory) so a restart does not lose scans, identification
/// results, accepted suggestions, or a recent dry-run approval.
/// </summary>
/// <remarks>
/// The scan and the dry run are stored in separate files so invalidating an
/// approval is a single delete. Anything that cannot be read cleanly is moved
/// aside and treated as absent: losing state only costs a rescan or a new dry
/// run, whereas loading a partial plan could misstate what PIM will do.
/// </remarks>
public sealed class JsonWorkflowStateStore : IWorkflowStateStore
{
    public const int SchemaVersion = 1;

    private const string ScanFileName = "scan.json";
    private const string DryRunFileName = "dry-run.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _syncRoot = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<JsonWorkflowStateStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _scanPath;
    private readonly string _dryRunPath;

    private bool _loaded;
    private List<Movie>? _movies;
    private string? _moviesSourceRoot;
    private DryRunPreviewResult? _dryRunPreview;
    private DryRunApproval? _dryRunApproval;

    public JsonWorkflowStateStore(
        IConfiguration configuration,
        ILogger<JsonWorkflowStateStore> logger)
        : this(configuration, logger, TimeProvider.System)
    {
    }

    public JsonWorkflowStateStore(
        IConfiguration configuration,
        ILogger<JsonWorkflowStateStore>? logger,
        TimeProvider timeProvider)
    {
        _configuration = configuration;
        _logger = logger ?? NullLogger<JsonWorkflowStateStore>.Instance;
        _timeProvider = timeProvider;

        var configured = configuration["PIM:WorkflowStateDirectory"]?.Trim();

        Directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Plex Integrity Manager",
                "Workflow")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));

        _scanPath = Path.Combine(Directory, ScanFileName);
        _dryRunPath = Path.Combine(Directory, DryRunFileName);
    }

    public string Directory { get; }

    public bool TryGetMovies(out List<Movie> movies)
    {
        lock (_syncRoot)
        {
            EnsureLoaded();

            if (_movies != null &&
                !PathsEqual(_moviesSourceRoot, CurrentSourceRoot()))
            {
                _logger.LogWarning(
                    "The saved scan belongs to a different source folder than the one configured; it was discarded.");
                ClearMoviesCore();
            }

            movies = _movies ?? new List<Movie>();
            return _movies != null;
        }
    }

    public void SaveMovies(List<Movie> movies, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(movies);

        lock (_syncRoot)
        {
            EnsureLoaded();

            _movies = movies;
            _moviesSourceRoot = sourceRoot ?? string.Empty;

            TryWrite(
                _scanPath,
                new ScanDocument
                {
                    SchemaVersion = SchemaVersion,
                    SavedUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    SourceRoot = _moviesSourceRoot,
                    Movies = movies
                },
                "scan");
        }
    }

    public void ClearMovies()
    {
        lock (_syncRoot)
        {
            EnsureLoaded();
            ClearMoviesCore();
        }
    }

    public DryRunPreviewResult? GetDryRunPreview()
    {
        lock (_syncRoot)
        {
            EnsureLoaded();
            ExpireDryRunIfNeeded();
            return _dryRunPreview;
        }
    }

    public DryRunApproval? GetDryRunApproval()
    {
        lock (_syncRoot)
        {
            EnsureLoaded();
            ExpireDryRunIfNeeded();
            return _dryRunApproval;
        }
    }

    public void SaveDryRun(DryRunPreviewResult preview, DryRunApproval approval)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(approval);

        lock (_syncRoot)
        {
            EnsureLoaded();

            _dryRunPreview = preview;
            _dryRunApproval = approval;

            TryWrite(
                _dryRunPath,
                new DryRunDocument
                {
                    SchemaVersion = SchemaVersion,
                    Preview = preview,
                    Approval = approval
                },
                "dry-run approval");
        }
    }

    public void InvalidateDryRunApproval()
    {
        lock (_syncRoot)
        {
            EnsureLoaded();
            InvalidateDryRunCore();
        }
    }

    private void ClearMoviesCore()
    {
        _movies = null;
        _moviesSourceRoot = null;
        TryDelete(_scanPath, "scan");
        InvalidateDryRunCore();
    }

    private void InvalidateDryRunCore()
    {
        _dryRunPreview = null;
        _dryRunApproval = null;
        TryDelete(_dryRunPath, "dry-run approval");
    }

    private void ExpireDryRunIfNeeded()
    {
        if (_dryRunApproval != null &&
            _dryRunApproval.IsExpired(_timeProvider.GetUtcNow().UtcDateTime))
        {
            InvalidateDryRunCore();
        }
    }

    private string CurrentSourceRoot() =>
        _configuration["PIM:ScanPath"] ?? string.Empty;

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;

        var scan = TryRead<ScanDocument>(_scanPath, "scan");

        if (scan != null)
        {
            _movies = scan.Movies;
            _moviesSourceRoot = scan.SourceRoot;
            _logger.LogInformation(
                "Restored a saved scan with {MovieCount} movie(s) from {SavedUtc:u}.",
                scan.Movies.Count,
                scan.SavedUtc);
        }

        var dryRun = TryRead<DryRunDocument>(_dryRunPath, "dry-run approval");

        if (dryRun != null && _movies != null)
        {
            _dryRunPreview = dryRun.Preview;
            _dryRunApproval = dryRun.Approval;
            ExpireDryRunIfNeeded();
        }
        else if (dryRun != null)
        {
            // An approval without the scan it approved can never match a plan.
            TryDelete(_dryRunPath, "dry-run approval");
        }
    }

    private T? TryRead<T>(string path, string description)
        where T : class, IWorkflowDocument
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<T>(json, SerializerOptions);

            if (document == null || document.SchemaVersion != SchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported {description} file version.");

            document.Validate();
            return document;
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or InvalidDataException
                or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(
                ex,
                "The saved {Description} could not be read and was set aside; it will not be used.",
                description);
            SetAside(path);
            return null;
        }
    }

    private void SetAside(string path)
    {
        try
        {
            File.Move(
                path,
                $"{path}.corrupt-{_timeProvider.GetUtcNow().UtcDateTime:yyyyMMddHHmmss}",
                overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path, "unreadable workflow state");
        }
    }

    private void TryWrite(string path, object document, string description)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var json = JsonSerializer.Serialize(document, SerializerOptions);

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory state is still correct for this session. Remove any
            // older file so a restart cannot resurrect state that was replaced.
            _logger.LogWarning(
                ex,
                "The {Description} could not be saved to disk and will not survive a restart.",
                description);
            TryDelete(path, description);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                // Temporary cleanup must not hide the save result.
            }
        }
    }

    private void TryDelete(string path, string description)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "The saved {Description} could not be removed from disk.",
                description);
        }
    }

    private static bool PathsEqual(string? firstPath, string? secondPath)
    {
        if (string.IsNullOrWhiteSpace(firstPath) ||
            string.IsNullOrWhiteSpace(secondPath))
        {
            return string.IsNullOrWhiteSpace(firstPath) &&
                   string.IsNullOrWhiteSpace(secondPath);
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(firstPath.Trim()).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                Path.GetFullPath(secondPath.Trim()).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private interface IWorkflowDocument
    {
        int SchemaVersion { get; }

        void Validate();
    }

    private sealed class ScanDocument : IWorkflowDocument
    {
        public int SchemaVersion { get; set; }

        public DateTime SavedUtc { get; set; }

        public string SourceRoot { get; set; } = string.Empty;

        public List<Movie> Movies { get; set; } = new();

        public void Validate()
        {
            if (Movies == null ||
                Movies.Any(movie =>
                    movie == null ||
                    movie.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(movie.OriginalFilePath)) ||
                Movies.Select(movie => movie.Id).Distinct().Count() != Movies.Count)
            {
                throw new InvalidDataException(
                    "The saved scan contains incomplete or duplicate movies.");
            }
        }
    }

    private sealed class DryRunDocument : IWorkflowDocument
    {
        public int SchemaVersion { get; set; }

        public DryRunPreviewResult? Preview { get; set; }

        public DryRunApproval? Approval { get; set; }

        public void Validate()
        {
            if (Preview == null ||
                Approval == null ||
                string.IsNullOrWhiteSpace(Approval.PlanFingerprint))
            {
                throw new InvalidDataException(
                    "The saved dry run is incomplete.");
            }
        }
    }
}
