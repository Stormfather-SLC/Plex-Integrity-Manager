using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IMovieConflictDetectionService
    {
        void ClearConflictState(List<Movie> movies);

        void ApplyConflictDetection(List<Movie> movies, string outputPath);
    }
}