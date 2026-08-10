using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Server;

namespace Vyral.Tests.Local;

public class EmbeddingJobStoreTests
{
    [Fact]
    public async Task EmbeddingJob_ExposesPartialProgressAndCancels()
    {
        var provider = new BlockingEmbeddingProvider();
        var store = new EmbeddingJobStore(new EmbeddingJobStoreOptions
        {
            MaxActiveJobs = 2,
            DefaultListLimit = 10,
            MaxListLimit = 10,
            MaxRetainedTerminalJobs = 10
        });

        var accepted = store.Start(new EmbeddingRequest
        {
            Texts = new List<string> { "alpha", "beta" }
        }, provider, new EmbeddingProviderOptions());

        await provider.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = store.Get(accepted.Id);
        Assert.NotNull(running);
        Assert.Equal(EmbeddingJobStatuses.Running, running!.Status);
        Assert.Equal(1, running.Attempted);
        Assert.Equal(0.5, running.Progress, precision: 3);
        Assert.Equal(1, running.CurrentIndex);
        Assert.NotNull(running.Result);
        Assert.Single(running.Result!.Items);

        var cancelRequested = store.Cancel(accepted.Id);
        Assert.NotNull(cancelRequested);
        Assert.True(cancelRequested!.CancellationRequested);

        EmbeddingJob? cancelled = null;
        for (var i = 0; i < 50; i++)
        {
            cancelled = store.Get(accepted.Id);
            if (cancelled?.Status == EmbeddingJobStatuses.Cancelled)
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(cancelled);
        Assert.Equal(EmbeddingJobStatuses.Cancelled, cancelled!.Status);
        Assert.True(cancelled.CancellationRequested);
        Assert.Equal("cancelled", cancelled.FailureClass);
        Assert.NotNull(cancelled.Result);
        Assert.Single(cancelled.Result!.Items);
    }

    private sealed class BlockingEmbeddingProvider : IEmbeddingProvider
    {
        private int _calls;

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "test-blocking";

        public string ModelId => "test-blocking-v1";

        public int Dimensions => 2;

        public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                return new[] { 1f, 0f };
            }

            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new[] { 0f, 1f };
        }
    }
}
