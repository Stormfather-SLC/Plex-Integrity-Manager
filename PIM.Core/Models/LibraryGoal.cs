namespace PIM.Core.Models;

public enum LibraryGoal
{
    Consolidation = 0,
    ReorganizationMigration = 1
}

public static class LibraryGoalSettings
{
    public static LibraryGoal Parse(string? value)
    {
        return Enum.TryParse<LibraryGoal>(
                   value,
                   ignoreCase: true,
                   out var parsed) &&
               Enum.IsDefined(parsed)
            ? parsed
            : LibraryGoal.Consolidation;
    }

    public static string GetDisplayName(LibraryGoal goal)
    {
        return goal == LibraryGoal.ReorganizationMigration
            ? "Reorganization / Migration"
            : "Consolidation";
    }

    public static string GetDescription(LibraryGoal goal)
    {
        return goal == LibraryGoal.ReorganizationMigration
            ? "Rename, reorganize, or move an existing Plex library while preserving Plex awareness."
            : "Identify duplicates and protect movies already represented in Plex for deliberate review.";
    }
}
