using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Caching.Memory;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Web.Pages;

public class DestinationProfilesModel : PageModel
{
    private const string DryRunPreviewCacheKey = "DryRunPreview";
    private const string DryRunApprovalCacheKey = "DryRunApproval";

    private readonly IDestinationProfileStore _profileStore;
    private readonly IDestinationPathBuilder _pathBuilder;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;

    public DestinationProfilesModel(
        IDestinationProfileStore profileStore,
        IDestinationPathBuilder pathBuilder,
        IConfiguration configuration,
        IMemoryCache cache)
    {
        _profileStore = profileStore;
        _pathBuilder = pathBuilder;
        _configuration = configuration;
        _cache = cache;
    }

    public IReadOnlyList<DestinationProfile> Profiles { get; private set; }
        = Array.Empty<DestinationProfile>();

    public Guid ActiveProfileId { get; private set; }

    [BindProperty]
    public DestinationProfile Profile { get; set; } = new();

    [BindProperty]
    public OrganizationLevelType NewLevelType { get; set; }
        = OrganizationLevelType.MpaRating;

    public DestinationPathResult? Preview { get; private set; }

    public string? PreviewError { get; private set; }

    public List<SelectListItem> LevelTypeOptions { get; } = new()
    {
        new("MPA Rating", OrganizationLevelType.MpaRating.ToString()),
        new("Primary Genre", OrganizationLevelType.PrimaryGenre.ToString()),
        new("Library Category", OrganizationLevelType.LibraryCategory.ToString()),
        new("Alphabetical Range", OrganizationLevelType.AlphabeticalRange.ToString()),
        new("Fixed Folder", OrganizationLevelType.FixedFolder.ToString()),
        new("Preserve Source Folders", OrganizationLevelType.PreserveSourceFolders.ToString())
    };

    public void OnGet(Guid? id)
    {
        LoadPage(id);
    }

    public IActionResult OnPostCreate()
    {
        var active = _profileStore.GetActiveProfile();

        var profile = new DestinationProfile
        {
            Name = "New Destination Profile",
            DestinationRoot = active.DestinationRoot
        };

        var saved = _profileStore.Save(profile);
        TempData["ProfileMessage"] = "New destination profile created.";
        return RedirectToPage(new { id = saved.Id });
    }

    public IActionResult OnPostSave()
    {
        NormalizeBoundProfile();

        if (!TryValidateProfile(Profile, out var validationMessage))
        {
            TempData["ProfileMessage"] = validationMessage;
            return RedirectToPage(new { id = Profile.Id });
        }

        var saved = _profileStore.Save(Profile);
        InvalidateDryRunApproval();

        TempData["ProfileMessage"] =
            $"'{saved.Name}' was saved. Existing dry-run approval was cleared.";

        return RedirectToPage(new { id = saved.Id });
    }

    public IActionResult OnPostAddLevel()
    {
        NormalizeBoundProfile();

        if (Profile.OrganizationLevels.Count >=
            DestinationProfile.MaximumOrganizationLevels)
        {
            TempData["ProfileMessage"] =
                $"A destination profile can contain up to {DestinationProfile.MaximumOrganizationLevels} organization levels.";
            return RedirectToPage(new { id = Profile.Id });
        }

        if (NewLevelType != OrganizationLevelType.FixedFolder &&
            Profile.OrganizationLevels.Any(level => level.Type == NewLevelType))
        {
            TempData["ProfileMessage"] =
                $"{DestinationOrganizationLevel.Create(NewLevelType).DisplayName} is already in this profile.";
            return RedirectToPage(new { id = Profile.Id });
        }

        Profile.OrganizationLevels.Add(
            DestinationOrganizationLevel.Create(NewLevelType));

        var saved = _profileStore.Save(Profile);
        InvalidateDryRunApproval();
        return RedirectToPage(new { id = saved.Id });
    }

    public IActionResult OnPostRemoveLevel(int index)
    {
        NormalizeBoundProfile();

        if (index >= 0 && index < Profile.OrganizationLevels.Count)
            Profile.OrganizationLevels.RemoveAt(index);

        var saved = _profileStore.Save(Profile);
        InvalidateDryRunApproval();
        return RedirectToPage(new { id = saved.Id });
    }

    public IActionResult OnPostMoveLevel(int index, string direction)
    {
        NormalizeBoundProfile();

        var targetIndex = direction.Equals("up", StringComparison.OrdinalIgnoreCase)
            ? index - 1
            : index + 1;

        if (index >= 0 &&
            index < Profile.OrganizationLevels.Count &&
            targetIndex >= 0 &&
            targetIndex < Profile.OrganizationLevels.Count)
        {
            (Profile.OrganizationLevels[index], Profile.OrganizationLevels[targetIndex]) =
                (Profile.OrganizationLevels[targetIndex], Profile.OrganizationLevels[index]);
        }

        var saved = _profileStore.Save(Profile);
        InvalidateDryRunApproval();
        return RedirectToPage(new { id = saved.Id });
    }

    public IActionResult OnPostDuplicate(Guid profileId)
    {
        var source = _profileStore.GetProfile(profileId);

        if (source == null)
        {
            TempData["ProfileMessage"] = "The destination profile could not be found.";
            return RedirectToPage();
        }

        var copy = new DestinationProfile
        {
            Name = $"{source.Name} Copy",
            DestinationRoot = source.DestinationRoot,
            OrganizationLevels = source.OrganizationLevels
                .Select(CloneLevel)
                .ToList()
        };

        var saved = _profileStore.Save(copy);
        TempData["ProfileMessage"] = $"'{source.Name}' was duplicated.";
        return RedirectToPage(new { id = saved.Id });
    }

    public IActionResult OnPostDelete(Guid profileId)
    {
        if (!_profileStore.Delete(profileId))
        {
            TempData["ProfileMessage"] =
                "PIM must keep at least one destination profile, or the profile was not found.";
            return RedirectToPage(new { id = profileId });
        }

        InvalidateDryRunApproval();
        TempData["ProfileMessage"] = "Destination profile deleted.";
        return RedirectToPage();
    }

    public IActionResult OnPostUse(Guid profileId)
    {
        NormalizeBoundProfile();

        if (Profile.Id != profileId)
        {
            TempData["ProfileMessage"] = "The destination profile selection did not match the editor.";
            return RedirectToPage(new { id = profileId });
        }

        if (!TryValidateProfile(Profile, out var validationMessage))
        {
            TempData["ProfileMessage"] = validationMessage;
            return RedirectToPage(new { id = Profile.Id });
        }

        // Save current editor changes before selecting the profile so the scan
        // always uses exactly what the user sees on this page.
        var saved = _profileStore.Save(Profile);

        if (!_profileStore.SetActive(saved.Id))
        {
            TempData["ProfileMessage"] = "The destination profile could not be selected.";
            return RedirectToPage(new { id = saved.Id });
        }

        InvalidateDryRunApproval();
        return RedirectToPage("/Index");
    }

    private void LoadPage(Guid? requestedProfileId)
    {
        Profiles = _profileStore.GetProfiles();
        var active = _profileStore.GetActiveProfile();
        ActiveProfileId = active.Id;

        Profile = requestedProfileId.HasValue
            ? _profileStore.GetProfile(requestedProfileId.Value) ?? active
            : active;

        BuildPreview();
    }

    private void BuildPreview()
    {
        try
        {
            var sourceRoot = _configuration["PIM:ScanPath"];

            if (string.IsNullOrWhiteSpace(sourceRoot))
                sourceRoot = @"X:\Incoming";

            var sampleDirectory = Path.Combine(
                sourceRoot,
                "01-Kids & Family");

            var sampleMovie = new Movie
            {
                Title = "Better Off Dead",
                Year = 1985,
                ImdbId = "tt0088794",
                MpaRating = "PG",
                Genres = new List<string> { "Comedy", "Romance" },
                PrimaryGenre = "Comedy",
                OriginalFilePath = Path.Combine(
                    sampleDirectory,
                    "Better.Off.Dead.1985.1080p.mp4"),
                DirectoryPath = sampleDirectory,
                FileName = "Better.Off.Dead.1985.1080p.mp4"
            };

            Preview = _pathBuilder.Build(
                sampleMovie,
                Profile,
                sourceRoot,
                ".mp4");
        }
        catch (Exception ex)
        {
            Preview = null;
            PreviewError = ex.Message;
        }
    }

    private void NormalizeBoundProfile()
    {
        Profile.OrganizationLevels ??= new List<DestinationOrganizationLevel>();

        foreach (var level in Profile.OrganizationLevels)
        {
            level.AlphabeticalBuckets ??= new List<AlphabeticalBucket>();
            level.Normalize();
        }
    }

    private void InvalidateDryRunApproval()
    {
        _cache.Remove(DryRunPreviewCacheKey);
        _cache.Remove(DryRunApprovalCacheKey);
    }

    private static bool TryValidateProfile(
        DestinationProfile profile,
        out string message)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            message = "Profile name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.DestinationRoot))
        {
            message = "Destination root folder is required.";
            return false;
        }

        if (profile.OrganizationLevels.Count >
            DestinationProfile.MaximumOrganizationLevels)
        {
            message =
                $"A profile can contain up to {DestinationProfile.MaximumOrganizationLevels} organization levels.";
            return false;
        }

        var duplicateDynamicLevel = profile.OrganizationLevels
            .Where(level => level.Type != OrganizationLevelType.FixedFolder)
            .GroupBy(level => level.Type)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateDynamicLevel != null)
        {
            message =
                $"{duplicateDynamicLevel.First().DisplayName} can appear only once in a profile.";
            return false;
        }

        var missingLiteral = profile.OrganizationLevels.FirstOrDefault(level =>
            (level.Type == OrganizationLevelType.FixedFolder ||
             level.Type == OrganizationLevelType.LibraryCategory) &&
            string.IsNullOrWhiteSpace(level.Value));

        if (missingLiteral != null)
        {
            message = $"{missingLiteral.DisplayName} requires a folder name.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static DestinationOrganizationLevel CloneLevel(
        DestinationOrganizationLevel source)
    {
        return new DestinationOrganizationLevel
        {
            Type = source.Type,
            Value = source.Value,
            UnknownFolderName = source.UnknownFolderName,
            AlphabeticalBuckets = source.AlphabeticalBuckets
                .Select(bucket => new AlphabeticalBucket
                {
                    FolderName = bucket.FolderName,
                    StartLetter = bucket.StartLetter,
                    EndLetter = bucket.EndLetter,
                    IncludeNumbersAndSymbols = bucket.IncludeNumbersAndSymbols
                })
                .ToList()
        };
    }
}
