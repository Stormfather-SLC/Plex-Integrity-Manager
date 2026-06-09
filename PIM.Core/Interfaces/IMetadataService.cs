using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IMetadataService
    {
        Task EnrichAsync(Movie movie);
    }
}