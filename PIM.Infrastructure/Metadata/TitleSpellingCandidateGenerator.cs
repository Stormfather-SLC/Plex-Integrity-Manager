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
        var matches = Regex.Matches(title, @"[A-Za-z0-9]+")
            .Cast<Match>()
            .Where(match =>
                match.Value.Length >= 4 &&
                !match.Value.Any(char.IsDigit))
            .ToList();

        // Prefer tokens the dictionary already considers misspelled. Only if
        // those produce no correction do we consider a dictionary-valid word
        // whose adjacent letters may have been transposed (for example,
        // "Loin" -> "Lion").
        foreach (var match in matches.Where(match =>
                     !IsDictionaryWord(match.Value.ToLowerInvariant())))
        {
            AddAdjacentTranspositions(title, match, candidates, seen);

            if (candidates.Count >= MaximumTitleCandidates)
                return candidates;

            AddClosestDictionarySuggestions(title, match, candidates, seen);

            if (candidates.Count >= MaximumTitleCandidates)
                return candidates;
        }

        foreach (var match in matches.Where(match =>
                     IsDictionaryWord(match.Value.ToLowerInvariant())))
        {
            AddAdjacentTranspositions(title, match, candidates, seen);

            if (candidates.Count >= MaximumTitleCandidates)
                return candidates;
        }

        return candidates;
    }

    private static void AddAdjacentTranspositions(
        string title,
        Match match,
        ICollection<SpellingCandidate> candidates,
        ISet<string> seen)
    {
        var token = match.Value;
        var normalizedToken = token.ToLowerInvariant();
        var transpositions = new List<(string Term, long Count)>();

        for (var index = 0; index < normalizedToken.Length - 1; index++)
        {
            if (normalizedToken[index] == normalizedToken[index + 1])
                continue;

            var characters = normalizedToken.ToCharArray();
            (characters[index], characters[index + 1]) =
                (characters[index + 1], characters[index]);
            var transposed = new string(characters);
            var count = GetDictionaryCount(transposed);

            // The general dictionary contains "incredible" but not the movie
            // title's plural "incredibles". Accept the transposition as locally
            // plausible when its singular form is a known word.
            if (count == 0 && transposed.EndsWith('s') && transposed.Length > 4)
                count = GetDictionaryCount(transposed[..^1]);

            if (count > 0)
                transpositions.Add((transposed, count));
        }

        foreach (var transposition in transpositions
                     .OrderByDescending(item => item.Count)
                     .ThenBy(item => item.Term, StringComparer.OrdinalIgnoreCase))
        {
            AddCandidate(
                title,
                match,
                transposition.Term,
                candidates,
                seen);

            if (candidates.Count >= MaximumTitleCandidates)
                return;
        }
    }

    private static void AddClosestDictionarySuggestions(
        string title,
        Match match,
        ICollection<SpellingCandidate> candidates,
        ISet<string> seen)
    {
        var normalizedToken = match.Value.ToLowerInvariant();
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
            AddCandidate(
                title,
                match,
                suggestion.term,
                candidates,
                seen);

            if (candidates.Count >= MaximumTitleCandidates)
                return;
        }
    }

    private static void AddCandidate(
        string title,
        Match match,
        string correctedToken,
        ICollection<SpellingCandidate> candidates,
        ISet<string> seen)
    {
        if (candidates.Count >= MaximumTitleCandidates)
            return;

        var correctedTitle = string.Concat(
            title.AsSpan(0, match.Index),
            PreserveCase(match.Value, correctedToken),
            title.AsSpan(match.Index + match.Length));

        if (seen.Add(correctedTitle))
            candidates.Add(new SpellingCandidate(correctedTitle, MaximumEditDistance));
    }

    private static bool IsDictionaryWord(string token) =>
        GetDictionaryCount(token) > 0;

    private static long GetDictionaryCount(string token) =>
        Dictionary.Value.Lookup(
                token,
                global::SymSpell.Verbosity.Top,
                maxEditDistance: 0)
            .FirstOrDefault(suggestion => suggestion.distance == 0)
            ?.count ?? 0;

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
