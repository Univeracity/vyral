using Grpc.Core;

namespace Vyral.Tests.Google;

public sealed class FirestoreExecutionStateStoreOptionsTests
{
    [Fact]
    public void ContentionTransactions_DefaultToAnExplicitBoundedRetryBudget()
    {
        var options = new FirestoreExecutionStateStoreOptions();

        var transactionOptions = options.BuildContentionTransactionOptions();

        Assert.Equal(FirestoreExecutionStateStoreOptions.DefaultContentionTransactionMaxAttempts, transactionOptions.MaxAttempts);
        Assert.InRange(
            transactionOptions.MaxAttempts,
            1,
            FirestoreExecutionStateStoreOptions.MaximumContentionTransactionMaxAttempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void ContentionTransactions_RejectUnboundedOrDisabledRetryBudgets(int attempts)
    {
        var options = new FirestoreExecutionStateStoreOptions
        {
            ContentionTransactionMaxAttempts = attempts
        };

        Assert.Throws<InvalidOperationException>(() => options.BuildContentionTransactionOptions());
    }

    [Fact]
    public void LeaseContention_NormalizesOnlyFirestoreAbortedFailures()
    {
        Assert.True(FirestoreExecutionStateStore.IsTransactionContention(
            new RpcException(new Status(StatusCode.Aborted, "contention"))));
        Assert.False(FirestoreExecutionStateStore.IsTransactionContention(
            new RpcException(new Status(StatusCode.PermissionDenied, "denied"))));
        Assert.False(FirestoreExecutionStateStore.IsTransactionContention(
            new InvalidOperationException("not an RPC failure")));
    }
}
