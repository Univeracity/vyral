using System.Text;
using System.Text.Json;
using System.Threading;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

internal static class LocalLexicalScorer
{
    private static readonly IReadOnlyList<string> DefaultFields = new[]
    {
        "/content",
        "/metadata",
        "/id",
        "/type",
        "/sources"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "by",
        "for",
        "from",
        "in",
        "is",
        "it",
        "of",
        "on",
        "or",
        "that",
        "the",
        "to",
        "with"
    };

    public static LexicalScoreResult Score(VyralRecord record, string query, LexicalSearchOptions? options)
    {
        return ScoreMany(new[] { record }, query, options).Single().Score;
    }

    public static List<LexicalScoredRecord> ScoreMany(
        IEnumerable<VyralRecord> records,
        string query,
        LexicalSearchOptions? options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Lexical search query is required.");
        }

        ct.ThrowIfCancellationRequested();
        var normalized = LexicalScoringOptions.From(options);
        var queryParts = ParseQuery(query);
        var queryTerms = NormalizeQueryTerms(queryParts.Tokens);
        var documents = new List<LexicalDocument>();
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            documents.Add(BuildDocument(record, normalized.Fields));
        }

        documents = documents
            .Where(document => RequiredPhraseGroupsMatch(document, normalized.RequiredPhraseGroups, out _))
            .ToList();

        var corpusDocumentCount = documents.Count;
        var averageDocumentLength = corpusDocumentCount == 0 ? 0 : documents.Average(document => document.Length);
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in queryTerms)
        {
            ct.ThrowIfCancellationRequested();
            documentFrequency[term] = documents.Count(document => TermMatchesDocument(document, term, normalized));
        }

        var termIdf = documentFrequency.ToDictionary(
            pair => pair.Key,
            pair => CalculateIdf(corpusDocumentCount, pair.Value),
            StringComparer.Ordinal);

        var rawScores = new List<LexicalScoreResult>();
        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (!RequiredPhraseGroupsMatch(document, normalized.RequiredPhraseGroups, out var matchedRequiredPhraseGroups))
            {
                continue;
            }
            rawScores.Add(ScoreDocument(document, queryParts, queryTerms, termIdf, averageDocumentLength, corpusDocumentCount, normalized));
            rawScores[^1].MatchedRequiredPhraseGroups = matchedRequiredPhraseGroups;
        }

        var maxRawScore = rawScores.Count == 0 ? 0 : rawScores.Max(score => score.RawScore);

        var scored = new List<LexicalScoredRecord>();
        foreach (var raw in rawScores)
        {
            ct.ThrowIfCancellationRequested();
            if (normalized.MatchMode == "all" && raw.MatchedTerms.Count < queryTerms.Count)
            {
                continue;
            }

            var normalizedBase = NormalizeBaseScore(raw, maxRawScore, normalized.Scoring);
            var finalScore = Math.Min(1.0f, normalizedBase + raw.PhraseBoost + raw.ExactBoost + raw.MetadataBoost);
            scored.Add(new LexicalScoredRecord(raw.Record, raw with
            {
                Score = finalScore,
                BaseScore = normalizedBase
            }));
        }

        return scored;
    }

    private static LexicalScoreResult ScoreDocument(
        LexicalDocument document,
        LexicalQueryParts query,
        List<string> queryTerms,
        IReadOnlyDictionary<string, float> termIdf,
        double averageDocumentLength,
        int corpusDocumentCount,
        LexicalScoringOptions options)
    {
        var matchedTerms = new HashSet<string>(StringComparer.Ordinal);
        var matchedFields = new HashSet<string>(StringComparer.Ordinal);
        var termScores = new Dictionary<string, float>(StringComparer.Ordinal);
        var totalTermHits = 0.0f;
        var phraseMatched = false;
        var exactMatched = false;
        var metadataMatched = false;
        var maxFieldBoost = 1.0f;
        var rawScore = 0.0f;
        var matchedPhrases = new HashSet<string>(StringComparer.Ordinal);
        var matchedPrefixTerms = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in document.Fields)
        {
            var fieldMatched = false;
            var fieldBoost = GetFieldBoost(field.Path, options.FieldBoosts);
            maxFieldBoost = Math.Max(maxFieldBoost, fieldBoost);

            foreach (var term in queryTerms)
            {
                var frequency = GetTermFrequency(field, term, options, out var prefixMatched);
                if (frequency <= 0)
                {
                    continue;
                }

                matchedTerms.Add(term);
                if (prefixMatched)
                {
                    matchedPrefixTerms.Add(term);
                }

                totalTermHits += frequency;
                fieldMatched = true;
            }

            foreach (var phrase in query.Phrases)
            {
                if (field.NormalizedText.Contains(phrase, StringComparison.Ordinal))
                {
                    phraseMatched = true;
                    matchedPhrases.Add(phrase);
                    fieldMatched = true;
                }
            }

            if (query.NormalizedText.Length > 0 && field.NormalizedText.Contains(query.NormalizedText, StringComparison.Ordinal))
            {
                phraseMatched = true;
                fieldMatched = true;
            }

            if ((query.NormalizedText.Length > 0 && string.Equals(field.NormalizedText, query.NormalizedText, StringComparison.Ordinal)) ||
                query.Phrases.Any(phrase => string.Equals(field.NormalizedText, phrase, StringComparison.Ordinal)))
            {
                exactMatched = true;
                fieldMatched = true;
            }

            if (!fieldMatched)
            {
                continue;
            }

            matchedFields.Add(field.Path);
            metadataMatched = metadataMatched || IsMetadataLikeField(field.Path);
        }

        foreach (var term in queryTerms)
        {
            var weightedFrequency = 0.0f;
            foreach (var field in document.Fields)
            {
                var frequency = GetTermFrequency(field, term, options, out _);
                if (frequency > 0)
                {
                    weightedFrequency += frequency * GetFieldBoost(field.Path, options.FieldBoosts);
                }
            }

            if (weightedFrequency <= 0)
            {
                continue;
            }

            var termScore = options.Scoring == "coverage"
                ? weightedFrequency
                : CalculateBm25TermScore(
                    weightedFrequency,
                    Math.Max(1, document.Length),
                    averageDocumentLength <= 0 ? Math.Max(1, document.Length) : averageDocumentLength,
                    termIdf[term],
                    options.Bm25K1,
                    options.Bm25B);

            termScores[term] = termScore;
            rawScore += termScore;
        }

        var termCoverage = queryTerms.Count == 0 ? 0 : matchedTerms.Count / (float)queryTerms.Count;
        var frequencyScore = queryTerms.Count == 0 ? 0 : Math.Min(1.0f, totalTermHits / queryTerms.Count);
        var phraseBoost = phraseMatched ? options.PhraseBoost : 0;
        var exactBoost = exactMatched ? options.ExactBoost : 0;
        var metadataBoost = metadataMatched && (matchedTerms.Count > 0 || phraseMatched || exactMatched)
            ? options.MetadataBoost
            : 0;

        if (options.Scoring == "coverage")
        {
            rawScore = (0.80f * termCoverage) + (0.20f * frequencyScore);
        }

        return new LexicalScoreResult
        {
            Record = document.Record,
            Score = 0,
            BaseScore = rawScore,
            RawScore = rawScore,
            TermCoverage = termCoverage,
            FrequencyScore = frequencyScore,
            PhraseBoost = phraseBoost,
            ExactBoost = exactBoost,
            MetadataBoost = metadataBoost,
            MaxFieldBoost = maxFieldBoost,
            DocumentLength = document.Length,
            AverageDocumentLength = averageDocumentLength,
            CorpusDocumentCount = corpusDocumentCount,
            MatchedFields = matchedFields.OrderBy(field => field, StringComparer.Ordinal).ToList(),
            MatchedTerms = matchedTerms.OrderBy(term => term, StringComparer.Ordinal).ToList(),
            MatchedPhrases = matchedPhrases.OrderBy(phrase => phrase, StringComparer.Ordinal).ToList(),
            MatchedPrefixTerms = matchedPrefixTerms.OrderBy(term => term, StringComparer.Ordinal).ToList(),
            Fields = options.Fields,
            TermIdf = termIdf.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            TermScores = termScores.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            FieldBoosts = options.FieldBoosts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Scoring = options.Scoring,
            MatchMode = options.MatchMode,
            PrefixMatching = options.PrefixMatching,
            PrefixMinChars = options.PrefixMinChars
        };
    }

    internal static List<List<string>> NormalizeRequiredPhraseGroups(List<List<string>>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new List<List<string>>();
        }

        if (source.Count > 16)
        {
            throw new InvalidOperationException("Lexical requiredPhraseGroups supports at most 16 groups.");
        }

        var normalizedGroups = new List<List<string>>();
        foreach (var sourceGroup in source)
        {
            if (sourceGroup is null || sourceGroup.Count == 0)
            {
                throw new InvalidOperationException("Each lexical requiredPhraseGroups entry must contain at least one phrase.");
            }

            if (sourceGroup.Count > 16)
            {
                throw new InvalidOperationException("Each lexical requiredPhraseGroups entry supports at most 16 phrases.");
            }

            var normalized = sourceGroup
                .Select(NormalizeRequiredPhrase)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (normalized.Count == 0)
            {
                throw new InvalidOperationException("Each lexical requiredPhraseGroups entry must contain a non-empty phrase.");
            }

            normalizedGroups.Add(normalized);
        }

        return normalizedGroups;
    }

    private static bool RequiredPhraseGroupsMatch(
        LexicalDocument document,
        IReadOnlyList<List<string>> requiredPhraseGroups,
        out List<List<string>> matchedRequiredPhraseGroups)
    {
        matchedRequiredPhraseGroups = new List<List<string>>();
        foreach (var group in requiredPhraseGroups)
        {
            var matches = group
                .Where(phrase => document.Fields.Any(field => field.NormalizedPhraseText.Contains(phrase, StringComparison.Ordinal)))
                .ToList();
            if (matches.Count == 0)
            {
                return false;
            }

            matchedRequiredPhraseGroups.Add(matches);
        }

        return true;
    }

    private static LexicalDocument BuildDocument(VyralRecord record, IReadOnlyList<string> fields)
    {
        var extractedFields = ExtractTextFields(record, fields)
            .Select(field => BuildDocumentField(field.Path, field.Text))
            .ToList();
        var terms = extractedFields
            .SelectMany(field => field.TermFrequencies.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var length = extractedFields.Sum(field => field.Length);

        return new LexicalDocument(record, extractedFields, terms, length);
    }

    private static LexicalDocumentField BuildDocumentField(string path, string text)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;
        foreach (var term in Tokenize(text))
        {
            frequencies[term] = frequencies.GetValueOrDefault(term) + 1;
            length++;
        }

        return new LexicalDocumentField(path, NormalizeText(text), NormalizePhraseText(text), frequencies, length);
    }

    private static LexicalQueryParts ParseQuery(string query)
    {
        var phrases = ExtractQuotedPhrases(query)
            .Select(NormalizeText)
            .Where(phrase => phrase.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var normalizedText = NormalizeText(query.Replace('"', ' '));
        return new LexicalQueryParts(normalizedText, Tokenize(query).ToList(), phrases);
    }

    private static IEnumerable<string> ExtractQuotedPhrases(string query)
    {
        var start = -1;
        for (var index = 0; index < query.Length; index++)
        {
            if (query[index] != '"')
            {
                continue;
            }

            if (start < 0)
            {
                start = index + 1;
                continue;
            }

            if (index > start)
            {
                yield return query[start..index];
            }

            start = -1;
        }
    }

    private static bool TermMatchesDocument(LexicalDocument document, string term, LexicalScoringOptions options)
    {
        return document.Fields.Any(field => GetTermFrequency(field, term, options, out _) > 0);
    }

    private static int GetTermFrequency(LexicalDocumentField field, string term, LexicalScoringOptions options, out bool prefixMatched)
    {
        prefixMatched = false;
        if (field.TermFrequencies.TryGetValue(term, out var exactFrequency))
        {
            return exactFrequency;
        }

        if (!options.PrefixMatching || term.Length < options.PrefixMinChars)
        {
            return 0;
        }

        var prefixFrequency = field.TermFrequencies
            .Where(pair => pair.Key.StartsWith(term, StringComparison.Ordinal))
            .Sum(pair => pair.Value);
        prefixMatched = prefixFrequency > 0;
        return prefixFrequency;
    }

    private static float NormalizeBaseScore(LexicalScoreResult raw, float maxRawScore, string scoring)
    {
        if (raw.RawScore <= 0)
        {
            return 0;
        }

        return scoring == "coverage"
            ? Math.Clamp(raw.RawScore, 0, 1)
            : maxRawScore <= 0
                ? 0
                : Math.Clamp(raw.RawScore / maxRawScore, 0, 1);
    }

    private static float CalculateIdf(int documentCount, int documentFrequency)
    {
        if (documentCount <= 0 || documentFrequency <= 0)
        {
            return 0;
        }

        return (float)Math.Log(1.0 + ((documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));
    }

    private static float CalculateBm25TermScore(float termFrequency, int documentLength, double averageDocumentLength, float idf, float k1, float b)
    {
        var lengthRatio = documentLength / Math.Max(1.0, averageDocumentLength);
        var denominator = termFrequency + k1 * (1 - b + b * lengthRatio);
        return denominator <= 0 ? 0 : idf * ((termFrequency * (k1 + 1)) / (float)denominator);
    }

    private static float GetFieldBoost(string path, IReadOnlyDictionary<string, float> fieldBoosts)
    {
        var boost = 1.0f;
        var matchLength = -1;
        foreach (var (field, candidateBoost) in fieldBoosts)
        {
            if (!string.Equals(path, field, StringComparison.Ordinal) &&
                !path.StartsWith(field + "/", StringComparison.Ordinal))
            {
                continue;
            }

            if (field.Length <= matchLength)
            {
                continue;
            }

            boost = Math.Max(0, candidateBoost);
            matchLength = field.Length;
        }

        return boost;
    }

    private static IEnumerable<string> NormalizeFields(List<string>? fields)
    {
        var selected = fields?.Where(field => !string.IsNullOrWhiteSpace(field)).Select(field => field.Trim()).ToList();
        if (selected is null || selected.Count == 0)
        {
            selected = DefaultFields.ToList();
        }

        return selected.Distinct(StringComparer.Ordinal);
    }

    private static List<string> NormalizeQueryTerms(List<string> tokens)
    {
        var filtered = tokens
            .Where(term => term.Length > 1 && !StopWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return filtered.Count == 0
            ? tokens.Distinct(StringComparer.Ordinal).ToList()
            : filtered;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string NormalizeText(string text)
    {
        var builder = new StringBuilder();
        var previousWasSpace = false;
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            builder.Append(c);
            previousWasSpace = false;
        }

        return builder.ToString();
    }

    private static string NormalizeRequiredPhrase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Lexical required phrases must be non-empty.");
        }

        var normalized = NormalizePhraseText(text);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Lexical required phrases must contain at least one letter or digit.");
        }

        if (normalized.Length > 256)
        {
            throw new InvalidOperationException("Lexical required phrase cannot exceed 256 normalized characters.");
        }

        return normalized;
    }

    private static string NormalizePhraseText(string text) => string.Join(" ", Tokenize(text));

    private static IEnumerable<(string Path, string Text)> ExtractTextFields(VyralRecord record, IEnumerable<string> fields)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record));
        foreach (var field in fields)
        {
            if (!TryGetJsonPointerValue(document.RootElement, field, out var value))
            {
                continue;
            }

            foreach (var extracted in FlattenTextValues(value, field))
            {
                yield return extracted;
            }
        }
    }

    private static IEnumerable<(string Path, string Text)> FlattenTextValues(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                yield return (path, value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                yield return (path, value.ToString());
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    foreach (var nested in FlattenTextValues(item, $"{path}/{index}"))
                    {
                        yield return nested;
                    }

                    index++;
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    foreach (var nested in FlattenTextValues(property.Value, $"{path}/{EscapeJsonPointerSegment(property.Name)}"))
                    {
                        yield return nested;
                    }
                }
                break;
        }
    }

    private static bool TryGetJsonPointerValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var rawSegment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                {
                    return false;
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }

            return false;
        }

        return true;
    }

    private static string EscapeJsonPointerSegment(string segment)
    {
        return segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    private static bool IsMetadataLikeField(string path)
    {
        return path.StartsWith("/metadata", StringComparison.Ordinal) ||
            path.StartsWith("/sources", StringComparison.Ordinal) ||
            string.Equals(path, "/id", StringComparison.Ordinal) ||
            path.StartsWith("/id/", StringComparison.Ordinal);
    }

    private sealed record LexicalQueryParts(string NormalizedText, List<string> Tokens, List<string> Phrases);

    private sealed record LexicalDocument(VyralRecord Record, List<LexicalDocumentField> Fields, HashSet<string> Terms, int Length);

    private sealed record LexicalDocumentField(
        string Path,
        string NormalizedText,
        string NormalizedPhraseText,
        Dictionary<string, int> TermFrequencies,
        int Length);

    private sealed class LexicalScoringOptions
    {
        public List<string> Fields { get; init; } = new();
        public Dictionary<string, float> FieldBoosts { get; init; } = new(StringComparer.Ordinal);
        public string Scoring { get; init; } = "bm25";
        public string MatchMode { get; init; } = "any";
        public float Bm25K1 { get; init; } = 1.2f;
        public float Bm25B { get; init; } = 0.75f;
        public float PhraseBoost { get; init; } = 0.15f;
        public float ExactBoost { get; init; } = 0.25f;
        public float MetadataBoost { get; init; } = 0.10f;
        public bool PrefixMatching { get; init; }
        public int PrefixMinChars { get; init; } = 3;
        public List<List<string>> RequiredPhraseGroups { get; init; } = new();

        public static LexicalScoringOptions From(LexicalSearchOptions? options)
        {
            var scoring = string.IsNullOrWhiteSpace(options?.Scoring) ? "bm25" : options.Scoring.Trim().ToLowerInvariant();
            if (scoring is not ("bm25" or "coverage"))
            {
                throw new InvalidOperationException($"Lexical scoring '{options?.Scoring}' is not supported.");
            }

            var matchMode = string.IsNullOrWhiteSpace(options?.MatchMode) ? "any" : options.MatchMode.Trim().ToLowerInvariant();
            if (matchMode is not ("any" or "all"))
            {
                throw new InvalidOperationException($"Lexical matchMode '{options?.MatchMode}' is not supported.");
            }

            var k1 = options?.Bm25K1 ?? 1.2f;
            if (k1 <= 0)
            {
                throw new InvalidOperationException("Lexical bm25K1 must be greater than zero.");
            }

            var b = options?.Bm25B ?? 0.75f;
            if (b < 0 || b > 1)
            {
                throw new InvalidOperationException("Lexical bm25B must be between 0 and 1.");
            }

            var prefixMinChars = options?.PrefixMinChars ?? 3;
            if (prefixMinChars <= 0)
            {
                throw new InvalidOperationException("Lexical prefixMinChars must be greater than zero.");
            }

            return new LexicalScoringOptions
            {
                Fields = NormalizeFields(options?.Fields).ToList(),
                FieldBoosts = options?.FieldBoosts == null
                    ? new Dictionary<string, float>(StringComparer.Ordinal)
                    : options.FieldBoosts
                        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                        .ToDictionary(pair => pair.Key.Trim(), pair => Math.Max(0, pair.Value), StringComparer.Ordinal),
                Scoring = scoring,
                MatchMode = matchMode,
                Bm25K1 = k1,
                Bm25B = b,
                PhraseBoost = Math.Max(0, options?.PhraseBoost ?? 0.15f),
                ExactBoost = Math.Max(0, options?.ExactBoost ?? 0.25f),
                MetadataBoost = Math.Max(0, options?.MetadataBoost ?? 0.10f),
                PrefixMatching = options?.PrefixMatching ?? false,
                PrefixMinChars = prefixMinChars,
                RequiredPhraseGroups = NormalizeRequiredPhraseGroups(options?.RequiredPhraseGroups)
            };
        }
    }
}

internal sealed record LexicalScoredRecord(VyralRecord Record, LexicalScoreResult Score);

internal sealed record LexicalScoreResult
{
    public VyralRecord Record { get; init; } = null!;
    public float Score { get; init; }
    public float BaseScore { get; init; }
    public float RawScore { get; init; }
    public float TermCoverage { get; init; }
    public float FrequencyScore { get; init; }
    public float PhraseBoost { get; init; }
    public float ExactBoost { get; init; }
    public float MetadataBoost { get; init; }
    public float MaxFieldBoost { get; init; }
    public int DocumentLength { get; init; }
    public double AverageDocumentLength { get; init; }
    public int CorpusDocumentCount { get; init; }
    public List<string> MatchedFields { get; init; } = new();
    public List<string> MatchedTerms { get; init; } = new();
    public List<string> MatchedPhrases { get; init; } = new();
    public List<string> MatchedPrefixTerms { get; init; } = new();
    public List<string> Fields { get; init; } = new();
    public Dictionary<string, float> TermIdf { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, float> TermScores { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, float> FieldBoosts { get; init; } = new(StringComparer.Ordinal);
    public string Scoring { get; init; } = "bm25";
    public string MatchMode { get; init; } = "any";
    public bool PrefixMatching { get; init; }
    public int PrefixMinChars { get; init; } = 3;
    public List<List<string>> MatchedRequiredPhraseGroups { get; set; } = new();

    public RetrievalDiagnostics ToDiagnostics(params string[] candidateSources)
    {
        return new RetrievalDiagnostics
        {
            CandidateSources = candidateSources
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(source => source, StringComparer.Ordinal)
                .ToList(),
            MatchedFields = MatchedFields,
            MatchedTerms = MatchedTerms,
            ScoreComponents = new Dictionary<string, float>
            {
                ["lexical"] = Score,
                ["lexicalBase"] = BaseScore,
                ["lexicalRaw"] = RawScore,
                ["termCoverage"] = TermCoverage,
                ["termFrequency"] = FrequencyScore,
                ["phraseBoost"] = PhraseBoost,
                ["exactBoost"] = ExactBoost,
                ["metadataBoost"] = MetadataBoost,
                ["maxFieldBoost"] = MaxFieldBoost
            },
            Details = new Dictionary<string, object?>
            {
                ["lexicalFields"] = Fields,
                ["lexicalScoring"] = Scoring,
                ["lexicalMatchMode"] = MatchMode,
                ["lexicalPrefixMatching"] = PrefixMatching,
                ["lexicalPrefixMinChars"] = PrefixMinChars,
                ["matchedRequiredPhraseGroups"] = MatchedRequiredPhraseGroups,
                ["matchedPhrases"] = MatchedPhrases,
                ["matchedPrefixTerms"] = MatchedPrefixTerms,
                ["termIdf"] = TermIdf,
                ["termScores"] = TermScores,
                ["fieldBoosts"] = FieldBoosts,
                ["documentLength"] = DocumentLength,
                ["averageDocumentLength"] = AverageDocumentLength,
                ["corpusDocumentCount"] = CorpusDocumentCount
            }
        };
    }
}
