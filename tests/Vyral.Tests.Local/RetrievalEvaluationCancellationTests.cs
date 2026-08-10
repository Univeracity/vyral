using System.Text.Json.Nodes;
using System.Threading;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public class RetrievalEvaluationCancellationTests
{
    [Fact]
    public async Task CompareAsync_PropagatesPreCanceledTokensWithoutRunningVariants()
    {
        var retrieval = new CountingRetrievalService();
        var evaluation = new LocalRetrievalEvaluationService(retrieval);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluation.CompareAsync(BuildComparisonRequest(), cts.Token));

        Assert.Equal(0, retrieval.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_PropagatesCancellationFromRetrieval()
    {
        using var cts = new CancellationTokenSource();
        var retrieval = new CancellingRetrievalService(cts);
        var evaluation = new LocalRetrievalEvaluationService(retrieval);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluation.EvaluateAsync(BuildEvaluationRequest(), cts.Token));

        Assert.Equal(1, retrieval.CallCount);
    }

    private static RetrievalEvaluationComparisonRequest BuildComparisonRequest()
    {
        return new RetrievalEvaluationComparisonRequest
        {
            Variants = new List<RetrievalEvaluationVariant>
            {
                new() { Id = "baseline", SearchMode = SearchModes.Lexical }
            },
            Cases = BuildEvaluationRequest().Cases
        };
    }

    private static RetrievalEvaluationRequest BuildEvaluationRequest()
    {
        return new RetrievalEvaluationRequest
        {
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "cancel",
                    Request = new RetrievalRequest
                    {
                        Query = "retention policy",
                        Collections = new List<string> { "chunks" },
                        SearchMode = SearchModes.Lexical
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "chunk-1" }
                    }
                }
            }
        };
    }

    private sealed class CountingRetrievalService : IRetrievalService
    {
        public int CallCount { get; private set; }

        public Task<RetrievalResultEnvelope> SearchAsync(RetrievalRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new RetrievalResultEnvelope
            {
                Query = request.Query,
                Trace = new JsonObject { ["searchMode"] = request.SearchMode ?? string.Empty }
            });
        }
    }

    private sealed class CancellingRetrievalService : IRetrievalService
    {
        private readonly CancellationTokenSource _cts;

        public CancellingRetrievalService(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public int CallCount { get; private set; }

        public async Task<RetrievalResultEnvelope> SearchAsync(RetrievalRequest request, CancellationToken ct = default)
        {
            CallCount++;
            await _cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new RetrievalResultEnvelope { Query = request.Query };
        }
    }
}
