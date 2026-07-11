using PIM.Core.Models;

namespace PIM.Core.Interfaces;

public interface IDestinationPathBuilder
{
    DestinationPathResult Build(
        Movie movie,
        DestinationProfile profile,
        string sourceRoot,
        string extension);
}
