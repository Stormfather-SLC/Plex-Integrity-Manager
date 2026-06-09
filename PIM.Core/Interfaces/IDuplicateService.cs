using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IDuplicateService
    {
        void Process(List<Movie> movies);
    }
}