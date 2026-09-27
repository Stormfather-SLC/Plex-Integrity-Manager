namespace PIM.Core.Models;

public enum MetadataLookupFailureType
{
    None,
    MissingLookupInput,
    MovieNotFound,
    RequestLimitReached,
    InvalidApiKey,
    OmdbError,
    HttpFailure,
    NetworkFailure,
    Timeout,
    MalformedResponse,
    MissingRequiredFields,
    LowConfidence,
    ImdbIdentityConflict,
    FuzzyCandidateNeedsReview,
    AmbiguousFuzzyCandidates,
    FuzzyCandidateYearConflict
}
