namespace PIM.Core.Models;

public enum MetadataMatchOrigin
{
    None,
    ImdbId,
    ExactTitleYear,
    YearRelaxedTitle,
    SpellCorrectedTitleYear,
    YearRelaxedSpellCorrection,
    FuzzySearchTitleYear,
    YearRelaxedFuzzySearch
}
