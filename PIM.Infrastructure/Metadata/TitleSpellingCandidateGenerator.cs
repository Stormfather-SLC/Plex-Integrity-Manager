using System.Reflection;
using System.Text.RegularExpressions;

namespace PIM.Infrastructure.Metadata;

internal static class TitleSpellingCandidateGenerator
{
    private const string DictionaryResourceName =
        "PIM.Infrastructure.Metadata.frequency_dictionary_en_82_765.txt";
    private const int MaximumTitleCandidates = 2;
    private const int MaximumEditDistance = 1;
    private static readonly Lazy<global::SymSpell> Dictionary = new(CreateDictionary);

    public static IReadOnlyList<SpellingCandidate> Generate(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Array.Empty<SpellingCandidate>();

        var candidates = new List<SpellingCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(title, @"[A-Za-z0-9]+");

        foreach (Match match in matches)
        {
            var token = match.Value;

            // Short and alphanumeric tokens are common in stylized movie titles
            // (WALL-E, Se7en, M3GAN). Do not attempt to rewrite them.
            if (token.Length < 4 || token.Any(char.IsDigit))
                continue;

            var normalizedToken = token.ToLowerInvariant();

            if (Dictionary.Value.Lookup(
                    normalizedToken,
                    global::SymSpell.Verbosity.Closest,
                    MaximumEditDistance).Any(suggestion =>
                        suggestion.distance == 0))
            {
                continue;
            }

            var suggestions = Dictionary.Value.Lookup(
                    normalizedToken,
                    global::SymSpell.Verbosity.Closest,
                    MaximumEditDistance)
                .Where(suggestion =>
                    suggestion.distance == MaximumEditDistance &&
                    !suggestion.term.Equals(
                        normalizedToken,
                        StringComparison.OrdinalIgnoreCase))
                .Take(MaximumTitleCandidates);

            foreach (var suggestion in suggestions)
            {
                var correctedToken = PreserveCase(token, suggestion.term);
                var correctedTitle = string.Concat(
                    title.AsSpan(0, match.Index),
                    correctedToken,
                    title.AsSpan(match.Index + match.Length));

                if (!seen.Add(correctedTitle))
                    continue;

                candidates.Add(new SpellingCandidate(
                    correctedTitle,
                    suggestion.distance));

                if (candidates.Count == MaximumTitleCandidates)
                    return candidates;
            }
        }

        return candidates;
    }

    private static global::SymSpell CreateDictionary()
    {
        var dictionary = new global::SymSpell(
            maxDictionaryEditDistance: MaximumEditDistance);
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(DictionaryResourceName)
            ?? throw new InvalidOperationException(
                "The embedded spelling dictionary could not be loaded.");

        if (!dictionary.LoadDictionary(stream, 0, 1, new[] { ' ' }))
        {
            throw new InvalidOperationException(
                "The embedded spelling dictionary contained no usable words.");
        }

        return dictionary;
    }

    private static string PreserveCase(string source, string suggestion)
    {
        if (source.All(char.IsUpper))
            return suggestion.ToUpperInvariant();

        if (char.IsUpper(source[0]))
            return char.ToUpperInvariant(suggestion[0]) + suggestion[1..];

        return suggestion;
    }

    internal sealed record SpellingCandidate(string Title, int EditDistance);
}
