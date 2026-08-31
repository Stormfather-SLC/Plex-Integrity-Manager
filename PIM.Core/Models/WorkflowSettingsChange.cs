namespace PIM.Core.Models;

public sealed record WorkflowSettingsChange(
    bool SourceChanged,
    bool DestinationChanged,
    bool WorkflowChanged)
{
    public bool ClearScan => SourceChanged;

    public bool RebuildExistingPlan =>
        !SourceChanged && (DestinationChanged || WorkflowChanged);

    public static WorkflowSettingsChange Evaluate(
        string previousSource,
        string currentSource,
        string previousDestination,
        string currentDestination,
        LibraryGoal previousWorkflow,
        LibraryGoal currentWorkflow)
    {
        return new WorkflowSettingsChange(
            !PathsEqual(previousSource, currentSource),
            !PathsEqual(previousDestination, currentDestination),
            previousWorkflow != currentWorkflow);
    }

    private static bool PathsEqual(string? firstPath, string? secondPath)
    {
        if (string.IsNullOrWhiteSpace(firstPath) ||
            string.IsNullOrWhiteSpace(secondPath))
        {
            return string.Equals(
                firstPath?.Trim(),
                secondPath?.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(firstPath).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                Path.GetFullPath(secondPath).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(
                firstPath.Trim(),
                secondPath.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
