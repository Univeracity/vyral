using System.Text.RegularExpressions;
using Vyral.Abstractions.Interfaces;

namespace Vyral.Local;

public sealed class LocalTokenOverlapRerankingService : IRerankingService
{
    private static readonly Regex TokenPattern = new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken ct = default)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new InvalidOperationException("Rerank query is required.");
        }

        if (request.Candidates.Count == 0)
        {
            throw new InvalidOperationException("Rerank requires at least one candidate.");
        }

        if (request.Limit <= 0)
        {
            throw new InvalidOperationException("Rerank limit must be greater than zero.");
        }

        var queryTerms = Tokenize(request.Query);
        var items = request.Candidates
            .Select(candidate =>
            {
                var terms = Tokenize(candidate.Text);
                var overlap = terms.Count == 0 ? 0 : queryTerms.Intersect(terms).Count();
                var score = queryTerms.Count == 0 ? 0 : (float)overlap / queryTerms.Count;
                return new
                {
                    candidate.Id,
                    Score = score,
                    candidate.OriginalRank
                };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.OriginalRank)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(request.Limit)
            .Select((item, index) => new RerankResultItem
            {
                Id = item.Id,
                Rank = index + 1,
                Score = item.Score
            })
            .ToList();

        return Task.FromResult(new RerankResult
        {
            Provider = "local-token-overlap-reranker",
            ModelId = "local-token-overlap-reranker-v1",
            Items = items
        });
    }

    private static HashSet<string> Tokenize(string text)
    {
        return TokenPattern
            .Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
