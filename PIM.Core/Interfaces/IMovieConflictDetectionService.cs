using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IMovieConflictDetectionService
    {
        void ClearDestinationConflictState(List<Movie> movies);

        void ClearConflictState(List<Movie> movies);

        void ApplyConflictDetection(List<Movie> movies, string outputPath);
    }
}
