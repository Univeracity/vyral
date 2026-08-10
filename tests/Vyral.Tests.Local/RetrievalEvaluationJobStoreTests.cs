using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Server;

namespace Vyral.Tests.Local;

public class RetrievalEvaluationJobStoreTests
{
    [Fact]
    public async Task EvaluationJob_ExposesPartialProgressAndCancels()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new BlockingEvaluationService(started);
        var store = new RetrievalEvaluationJobStore(new RetrievalEvaluationJobStoreOptions
        {
            MaxActiveJobs = 2,
            DefaultListLimit = 10,
            MaxListLimit = 10,
            MaxRetainedTerminalJobs = 10
        });

        var accepted = store.StartEvaluation(BuildEvaluationRequest(), service);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = store.Get(accepted.Id);
        Assert.NotNull(running);
        Assert.Equal(RetrievalEvaluationJobKinds.Evaluation, running!.Kind);
        Assert.Equal(RetrievalEvaluationJobStatuses.Running, running.Status);
        Assert.Equal(0.5, running.Progress, precision: 3);
        Assert.Equal(1, running.CasesAttempted);
        Assert.Equal(1, running.CasesSucceeded);
        Assert.Equal("case-a", running.CurrentCaseName);
        Assert.NotNull(running.EvaluationResult);
        Assert.Single(running.EvaluationResult!.Cases);
        Assert.Null(running.Result);

        var cancelRequested = store.Cancel(accepted.Id);
        Assert.NotNull(cancelRequested);
        Assert.True(cancelRequested!.CancellationRequested);

        RetrievalEvaluationJob? cancelled = null;
        for (var i = 0; i < 50; i++)
        {
            cancelled = store.Get(accepted.Id);
            if (cancelled?.Status == RetrievalEvaluationJobStatuses.Cancelled)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(cancelled);
        Assert.Equal(RetrievalEvaluationJobStatuses.Cancelled, cancelled!.Status);
        Assert.True(cancelled.CancellationRequested);
        Assert.Equal("cancelled", cancelled.FailureClass);
        Assert.NotNull(cancelled.EvaluationResult);
        Assert.Single(cancelled.EvaluationResult!.Cases);
    }

    [Fact]
    public async Task ComparisonJob_ExposesPartialProgressAndCancels()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new BlockingComparisonService(started);
        var store = new RetrievalEvaluationJobStore(new RetrievalEvaluationJobStoreOptions
        {
            MaxActiveJobs = 2,
            DefaultListLimit = 10,
            MaxListLimit = 10,
            MaxRetainedTerminalJobs = 10
        });

        var accepted = store.StartComparison(BuildRequest(), service);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = store.Get(accepted.Id);
        Assert.NotNull(running);
        Assert.Equal(RetrievalEvaluationJobStatuses.Running, running!.Status);
        Assert.Equal(0.5, running.Progress, precision: 3);
        Assert.Equal("variant-a", running.CurrentVariantId);
        Assert.NotNull(running.Result);
        Assert.Single(running.Result!.Variants);

        var cancelRequested = store.Cancel(accepted.Id);
        Assert.NotNull(cancelRequested);
        Assert.True(cancelRequested!.CancellationRequested);

        RetrievalEvaluationJob? cancelled = null;
        for (var i = 0; i < 50; i++)
        {
            cancelled = store.Get(accepted.Id);
            if (cancelled?.Status == RetrievalEvaluationJobStatuses.Cancelled)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(cancelled);
        Assert.Equal(RetrievalEvaluationJobStatuses.Cancelled, cancelled!.Status);
        Assert.True(cancelled.CancellationRequested);
        Assert.Equal("cancelled", cancelled.FailureClass);
        Assert.NotNull(cancelled.Result);
        Assert.Single(cancelled.Result!.Variants);
    }

    private static RetrievalEvaluationComparisonRequest BuildRequest()
    {
        return new RetrievalEvaluationComparisonRequest
        {
            Cases = new List<RetrievalEvaluationCase>
            {
                new()
                {
                    Name = "case-a",
                    Request = new RetrievalRequest
                    {
                        Query = "retention policy",
                        Collections = new List<string> { "chunks" }
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "chunk-a" }
                    }
                }
            },
            Variants = new List<RetrievalEvaluationVariant>
            {
                new() { Id = "variant-a" },
                new() { Id = "variant-b" }
            }
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
                    Name = "case-a",
                    Request = new RetrievalRequest
                    {
                        Query = "retention policy",
                        Collections = new List<string> { "chunks" }
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "chunk-a" }
                    }
                },
                new()
                {
                    Name = "case-b",
                    Request = new RetrievalRequest
                    {
                        Query = "travel policy",
                        Collections = new List<string> { "chunks" }
                    },
                    Expected = new List<RetrievalEvaluationExpectedMatch>
                    {
                        new() { Id = "chunk-b" }
                    }
                }
            }
        };
    }

    private sealed class BlockingEvaluationService : IRetrievalEvaluationService
    {
        private readonly TaskCompletionSource _started;

        public BlockingEvaluationService(TaskCompletionSource started)
        {
            _started = started;
        }

        public async Task<RetrievalEvaluationResult> EvaluateAsync(
            RetrievalEvaluationRequest request,
            CancellationToken ct = default,
            IProgress<RetrievalEvaluationProgress>? progress = null)
        {
            var result = new RetrievalEvaluationResult
            {
                Requested = request.Cases.Count,
                Attempted = 1,
                Succeeded = 1,
                HitCount = 1,
                Cases = new List<RetrievalEvaluationCaseResult>
                {
                    new()
                    {
                        Index = 0,
                        Name = "case-a",
                        Query = "retention policy",
                        Status = EvaluationCaseStatuses.Succeeded,
                        DurationMs = 12,
                        Hit = true
                    }
                }
            };

            progress?.Report(new RetrievalEvaluationProgress
            {
                CurrentCaseIndex = 0,
                CurrentCaseName = "case-a",
                Requested = result.Requested,
                CasesAttempted = result.Attempted,
                CasesSucceeded = result.Succeeded,
                CasesFailed = result.Failed,
                Result = result
            });
            _started.SetResult();

            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return result;
        }

        public Task<RetrievalEvaluationComparisonResult> CompareAsync(
            RetrievalEvaluationComparisonRequest request,
            CancellationToken ct = default,
            IProgress<RetrievalEvaluationComparisonProgress>? progress = null)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class BlockingComparisonService : IRetrievalEvaluationService
    {
        private readonly TaskCompletionSource _started;

        public BlockingComparisonService(TaskCompletionSource started)
        {
            _started = started;
        }

        public Task<RetrievalEvaluationResult> EvaluateAsync(
            RetrievalEvaluationRequest request,
            CancellationToken ct = default,
            IProgress<RetrievalEvaluationProgress>? progress = null)
        {
            throw new NotSupportedException();
        }

        public async Task<RetrievalEvaluationComparisonResult> CompareAsync(
            RetrievalEvaluationComparisonRequest request,
            CancellationToken ct = default,
            IProgress<RetrievalEvaluationComparisonProgress>? progress = null)
        {
            var result = new RetrievalEvaluationComparisonResult
            {
                Requested = request.Cases.Count,
                VariantsRequested = request.Variants.Count,
                VariantsAttempted = 1,
                VariantsSucceeded = 1,
                BaselineVariantId = "variant-a",
                Variants = new List<RetrievalEvaluationVariantResult>
                {
                    new()
                    {
                        Id = "variant-a",
                        Status = EvaluationVariantStatuses.Succeeded,
                        Metrics = new RetrievalEvaluationMetrics
                        {
                            Requested = request.Cases.Count,
                            Attempted = request.Cases.Count,
                            Succeeded = request.Cases.Count
                        }
                    }
                }
            };

            progress?.Report(new RetrievalEvaluationComparisonProgress
            {
                CurrentVariantId = "variant-a",
                CurrentVariantIndex = 0,
                Requested = result.Requested,
                VariantsRequested = result.VariantsRequested,
                VariantsAttempted = result.VariantsAttempted,
                VariantsSucceeded = result.VariantsSucceeded,
                VariantsFailed = result.VariantsFailed,
                Result = result
            });
            _started.SetResult();

            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return result;
        }
    }
}
