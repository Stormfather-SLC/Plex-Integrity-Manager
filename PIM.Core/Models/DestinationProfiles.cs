using System.Text.Json.Serialization;

namespace PIM.Core.Models;

/// <summary>
/// A single folder-producing step in a destination profile.
/// The order of the levels in DestinationProfile.OrganizationLevels is the
/// order in which folders are created beneath the destination root.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrganizationLevelType
{
    MpaRating,
    PrimaryGenre,
    LibraryCategory,
    AlphabeticalRange,
    FixedFolder,
    PreserveSourceFolders
}

public sealed class AlphabeticalBucket
{
    public string FolderName { get; set; } = string.Empty;

    public string? StartLetter { get; set; }

    public string? EndLetter { get; set; }

    public bool IncludeNumbersAndSymbols { get; set; }

    public static List<AlphabeticalBucket> CreateDefaultBuckets()
    {
        return new List<AlphabeticalBucket>
        {
            new() { FolderName = "#'s", IncludeNumbersAndSymbols = true },
            new() { FolderName = "A-C", StartLetter = "A", EndLetter = "C" },
            new() { FolderName = "D-F", StartLetter = "D", EndLetter = "F" },
            new() { FolderName = "G-I", StartLetter = "G", EndLetter = "I" },
            new() { FolderName = "J-L", StartLetter = "J", EndLetter = "L" },
            new() { FolderName = "M-O", StartLetter = "M", EndLetter = "O" },
            new() { FolderName = "P-R", StartLetter = "P", EndLetter = "R" },
            new() { FolderName = "S-V", StartLetter = "S", EndLetter = "V" },
            new() { FolderName = "W-Z", StartLetter = "W", EndLetter = "Z" }
        };
    }
}

public sealed class DestinationOrganizationLevel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public OrganizationLevelType Type { get; set; }

    /// <summary>
    /// Literal folder value used by FixedFolder and LibraryCategory.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Folder used when the metadata required by this level is unavailable.
    /// </summary>
    public string? UnknownFolderName { get; set; }

    public List<AlphabeticalBucket> AlphabeticalBuckets { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => Type switch
    {
        OrganizationLevelType.MpaRating => "MPA Rating",
        OrganizationLevelType.PrimaryGenre => "Primary Genre",
        OrganizationLevelType.LibraryCategory => "Library Category",
        OrganizationLevelType.AlphabeticalRange => "Alphabetical Range",
        OrganizationLevelType.FixedFolder => "Fixed Folder",
        OrganizationLevelType.PreserveSourceFolders => "Preserve Source Folders",
        _ => Type.ToString()
    };

    public static DestinationOrganizationLevel Create(OrganizationLevelType type)
    {
        var level = new DestinationOrganizationLevel { Type = type };
        level.Normalize();
        return level;
    }

    public void Normalize()
    {
        if (Id == Guid.Empty)
            Id = Guid.NewGuid();

        AlphabeticalBuckets ??= new List<AlphabeticalBucket>();
        Value = string.IsNullOrWhiteSpace(Value) ? null : Value.Trim();

        UnknownFolderName = string.IsNullOrWhiteSpace(UnknownFolderName)
            ? Type switch
            {
                OrganizationLevelType.MpaRating => "Unrated",
                OrganizationLevelType.PrimaryGenre => "Other",
                OrganizationLevelType.LibraryCategory => "Uncategorized",
                OrganizationLevelType.AlphabeticalRange => "#'s",
                OrganizationLevelType.PreserveSourceFolders => "Source",
                _ => null
            }
            : UnknownFolderName.Trim();

        if (Type == OrganizationLevelType.AlphabeticalRange &&
            AlphabeticalBuckets.Count == 0)
        {
            AlphabeticalBuckets = AlphabeticalBucket.CreateDefaultBuckets();
        }
    }
}

public sealed class DestinationProfile
{
    public const int MaximumOrganizationLevels = 5;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New Destination Profile";

    public string DestinationRoot { get; set; } = string.Empty;

    public int Revision { get; set; } = 1;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public List<DestinationOrganizationLevel> OrganizationLevels { get; set; } = new();

    [JsonIgnore]
    public string OrganizationSummary => OrganizationLevels == null || OrganizationLevels.Count == 0
        ? "Flat movie folder structure"
        : string.Join(" → ", OrganizationLevels.Select(level => level.DisplayName));

    public void Normalize()
    {
        if (Id == Guid.Empty)
            Id = Guid.NewGuid();

        OrganizationLevels ??= new List<DestinationOrganizationLevel>();

        Name = string.IsNullOrWhiteSpace(Name)
            ? "Unnamed Destination Profile"
            : Name.Trim();

        DestinationRoot = DestinationRoot?.Trim() ?? string.Empty;
        Revision = Math.Max(1, Revision);

        var normalizedLevels = new List<DestinationOrganizationLevel>();
        var usedDynamicTypes = new HashSet<OrganizationLevelType>();

        foreach (var level in OrganizationLevels
                     .Where(level => level != null)
                     .Take(MaximumOrganizationLevels))
        {
            level.Normalize();

            // Fixed folders may be used more than once. Every other dynamic
            // level is allowed only once so a profile cannot accidentally
            // create Rating/Genre/Rating-style paths.
            if (level.Type != OrganizationLevelType.FixedFolder &&
                !usedDynamicTypes.Add(level.Type))
            {
                continue;
            }

            normalizedLevels.Add(level);
        }

        OrganizationLevels = normalizedLevels;
    }
}

public sealed class DestinationPathResult
{
    public string DestinationRoot { get; init; } = string.Empty;

    public IReadOnlyList<string> OrganizationSegments { get; init; }
        = Array.Empty<string>();

    public string MovieFolderName { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string FullDirectoryPath { get; init; } = string.Empty;

    public string FullFilePath { get; init; } = string.Empty;

    public IReadOnlyList<string> Warnings { get; init; }
        = Array.Empty<string>();
}
