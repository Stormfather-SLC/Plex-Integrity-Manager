namespace PIM.Core.Models;

public enum LibraryGoal
{
    // Values 0 and 1 are retained for compatibility with persisted legacy
    // configuration that may have serialized the enum numerically.
    Consolidation = 0,
    ReorganizationMigration = 1,
    OrganizeNewMovies = 2
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
            : LibraryGoal.OrganizeNewMovies;
    }

    public static string GetDisplayName(LibraryGoal goal)
    {
        return goal switch
        {
            LibraryGoal.ReorganizationMigration => "Reorganize / Migrate",
            LibraryGoal.Consolidation => "Consolidate & Save Space",
            _ => "Organize New Movies"
        };
    }

    public static string GetDescription(LibraryGoal goal)
    {
        return goal switch
        {
            LibraryGoal.ReorganizationMigration =>
                "Safely rename or move movies Plex may already know, without treating that awareness alone as a blocker.",
            LibraryGoal.Consolidation =>
                "Identify redundant copies, preserve editions, and avoid unnecessary duplicate moves without deleting media.",
            _ =>
                "Safely identify new movies and block anything uncertain or already represented in Plex."
        };
    }
}
