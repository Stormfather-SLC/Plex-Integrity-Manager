using System.Text.Json;
using System.Text.Json.Serialization;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Web.Services;

/// <summary>
/// Local JSON-backed destination profile storage for the current desktop-hosted
/// PIM application. Profiles can move to a database later without changing the
/// path-builder or rename workflow contracts.
/// </summary>
public sealed class DestinationProfileStore : IDestinationProfileStore
{
    private readonly object _syncRoot = new();
    private readonly string _filePath;
    private readonly IConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions;

    public DestinationProfileStore(
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _configuration = configuration;

        var dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "destination-profiles.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public IReadOnlyList<DestinationProfile> GetProfiles()
    {
        lock (_syncRoot)
        {
            var document = LoadDocument();
            return document.Profiles
                .OrderBy(profile => profile.Name)
                .Select(Clone)
                .ToList();
        }
    }

    public DestinationProfile GetActiveProfile()
    {
        lock (_syncRoot)
        {
            var document = LoadDocument();
            var active = document.Profiles.FirstOrDefault(
                profile => profile.Id == document.ActiveProfileId);

            if (active == null)
            {
                active = document.Profiles.First();
                document.ActiveProfileId = active.Id;
                SaveDocument(document);
            }

            return Clone(active);
        }
    }

    public DestinationProfile? GetProfile(Guid profileId)
    {
        lock (_syncRoot)
        {
            var profile = LoadDocument().Profiles.FirstOrDefault(
                candidate => candidate.Id == profileId);

            return profile == null ? null : Clone(profile);
        }
    }

    public DestinationProfile Save(DestinationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_syncRoot)
        {
            var document = LoadDocument();
            profile.Normalize();

            var existingIndex = document.Profiles.FindIndex(
                candidate => candidate.Id == profile.Id);

            if (existingIndex >= 0)
            {
                profile.Revision = document.Profiles[existingIndex].Revision + 1;
                profile.UpdatedUtc = DateTime.UtcNow;
                document.Profiles[existingIndex] = Clone(profile);
            }
            else
            {
                profile.Revision = Math.Max(1, profile.Revision);
                profile.UpdatedUtc = DateTime.UtcNow;
                document.Profiles.Add(Clone(profile));
            }

            if (document.ActiveProfileId == Guid.Empty)
                document.ActiveProfileId = profile.Id;

            SaveDocument(document);
            return Clone(profile);
        }
    }

    public bool Delete(Guid profileId)
    {
        lock (_syncRoot)
        {
            var document = LoadDocument();

            // PIM must always retain at least one usable destination profile.
            if (document.Profiles.Count <= 1)
                return false;

            var removed = document.Profiles.RemoveAll(
                profile => profile.Id == profileId) > 0;

            if (!removed)
                return false;

            if (document.ActiveProfileId == profileId)
                document.ActiveProfileId = document.Profiles.First().Id;

            SaveDocument(document);
            return true;
        }
    }

    public bool SetActive(Guid profileId)
    {
        lock (_syncRoot)
        {
            var document = LoadDocument();

            if (!document.Profiles.Any(profile => profile.Id == profileId))
                return false;

            document.ActiveProfileId = profileId;
            SaveDocument(document);
            return true;
        }
    }

    private DestinationProfileDocument LoadDocument()
    {
        if (!File.Exists(_filePath))
        {
            var newDocument = CreateDefaultDocument();
            SaveDocument(newDocument);
            return newDocument;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var document = JsonSerializer.Deserialize<DestinationProfileDocument>(
                json,
                _jsonOptions);

            if (document?.Profiles == null || document.Profiles.Count == 0)
                throw new InvalidDataException("No destination profiles were found.");

            document.Profiles = document.Profiles
                .Where(profile => profile != null)
                .ToList();

            if (document.Profiles.Count == 0)
                throw new InvalidDataException("No valid destination profiles were found.");

            foreach (var profile in document.Profiles)
                profile.Normalize();

            if (!document.Profiles.Any(
                    profile => profile.Id == document.ActiveProfileId))
            {
                document.ActiveProfileId = document.Profiles.First().Id;
            }

            return document;
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or InvalidDataException)
        {
            var backupPath = $"{_filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";

            try
            {
                File.Copy(_filePath, backupPath, overwrite: true);
            }
            catch
            {
                // A failed backup should not prevent PIM from recovering with
                // safe default profiles.
            }

            var recoveredDocument = CreateDefaultDocument();
            SaveDocument(recoveredDocument);
            return recoveredDocument;
        }
    }

    private void SaveDocument(DestinationProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Profiles ??= new List<DestinationProfile>();

        if (document.Profiles.Count == 0)
            throw new InvalidOperationException("At least one destination profile is required.");

        foreach (var profile in document.Profiles)
            profile.Normalize();

        var json = JsonSerializer.Serialize(document, _jsonOptions);
        var temporaryPath = $"{_filePath}.tmp";

        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private DestinationProfileDocument CreateDefaultDocument()
    {
        var configuredRoot = _configuration["PIM:OutputPath"] ?? string.Empty;

        var flat = new DestinationProfile
        {
            Name = "Flat Movie Library",
            DestinationRoot = configuredRoot
        };

        var ratingThenGenre = new DestinationProfile
        {
            Name = "Rating then Genre",
            DestinationRoot = configuredRoot,
            OrganizationLevels = new List<DestinationOrganizationLevel>
            {
                DestinationOrganizationLevel.Create(
                    OrganizationLevelType.MpaRating),
                DestinationOrganizationLevel.Create(
                    OrganizationLevelType.PrimaryGenre)
            }
        };

        var genreThenRating = new DestinationProfile
        {
            Name = "Genre then Rating",
            DestinationRoot = configuredRoot,
            OrganizationLevels = new List<DestinationOrganizationLevel>
            {
                DestinationOrganizationLevel.Create(
                    OrganizationLevelType.PrimaryGenre),
                DestinationOrganizationLevel.Create(
                    OrganizationLevelType.MpaRating)
            }
        };

        var categoryThenAlphabetical = new DestinationProfile
        {
            Name = "Category then Alphabetical Range",
            DestinationRoot = configuredRoot,
            OrganizationLevels = new List<DestinationOrganizationLevel>
            {
                new()
                {
                    Type = OrganizationLevelType.LibraryCategory,
                    Value = "10-Everything Else",
                    UnknownFolderName = "10-Everything Else"
                },
                DestinationOrganizationLevel.Create(
                    OrganizationLevelType.AlphabeticalRange)
            }
        };

        var preserveSource = new DestinationProfile
        {
            Name = "Preserve Source Folders",
            DestinationRoot = configuredRoot,
            OrganizationLevels = new List<DestinationOrganizationLevel>
            {
                DestinationOrganizationLevel.Create(
                    OrganizationLevelType.PreserveSourceFolders)
            }
        };

        var profiles = new List<DestinationProfile>
        {
            flat,
            ratingThenGenre,
            genreThenRating,
            categoryThenAlphabetical,
            preserveSource
        };

        foreach (var profile in profiles)
            profile.Normalize();

        // Flat remains active on first launch so adding this feature does not
        // silently change an existing user's destination structure.
        return new DestinationProfileDocument
        {
            ActiveProfileId = flat.Id,
            Profiles = profiles
        };
    }

    private DestinationProfile Clone(DestinationProfile profile)
    {
        var json = JsonSerializer.Serialize(profile, _jsonOptions);
        return JsonSerializer.Deserialize<DestinationProfile>(json, _jsonOptions)
               ?? throw new InvalidOperationException(
                   "The destination profile could not be copied.");
    }

    private sealed class DestinationProfileDocument
    {
        public Guid ActiveProfileId { get; set; }

        public List<DestinationProfile>? Profiles { get; set; } = new();
    }
}
