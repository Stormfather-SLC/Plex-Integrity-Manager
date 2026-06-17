using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IDestinationConflictService
    {
        DestinationConflictResult Check(Movie movie, string outputPath);
    }
}