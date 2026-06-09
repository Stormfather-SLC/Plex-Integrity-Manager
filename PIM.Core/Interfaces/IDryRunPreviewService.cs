using PIM.Core.Models;

namespace PIM.Core.Interfaces;

public interface IDryRunPreviewService
{
    DryRunPreviewResult BuildPreview(IEnumerable<Movie> movies);
}