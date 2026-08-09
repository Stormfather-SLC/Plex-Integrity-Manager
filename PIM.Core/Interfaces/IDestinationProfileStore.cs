using PIM.Core.Models;

namespace PIM.Core.Interfaces;

public interface IDestinationProfileStore
{
    IReadOnlyList<DestinationProfile> GetProfiles();

    DestinationProfile GetActiveProfile();

    DestinationProfile? GetProfile(Guid profileId);

    DestinationProfile Save(DestinationProfile profile);

    bool Delete(Guid profileId);

    bool SetActive(Guid profileId);
}
