using System.Text.Json;
using Vyral.CanonicalStore.Benchmarks;

namespace Vyral.Tests.MySql;

public sealed class CanonicalMySqlBenchmarkPolicyTests
{
    [Fact]
    public void DecisionPolicy_KeepsMySqlOnlyWhenEveryHotTenantBudgetPasses()
    {
        var decision = CanonicalMySqlBenchmarkDecisionPolicy.Evaluate(
            Metrics(p95Ms: 80, operationsPerSecond: 60),
            Metrics(p95Ms: 25, operationsPerSecond: 200),
            Metrics(p95Ms: 30, operationsPerSecond: 100),
            Metrics(p95Ms: 10, operationsPerSecond: 300),
            Budgets());

        Assert.True(decision.WithinBudget);
        Assert.Equal("mysql_tenant_row", decision.RecommendedProfile);
        Assert.Equal(3.2, decision.WriteContentionRatio, precision: 6);
        Assert.Empty(decision.Violations);
    }

    [Fact]
    public void DecisionPolicy_RecommendsGranularStoreForLatencyThroughputContentionOrFailures()
    {
        var decision = CanonicalMySqlBenchmarkDecisionPolicy.Evaluate(
            Metrics(p95Ms: 150, operationsPerSecond: 40, failures: 1),
            Metrics(p95Ms: 20, operationsPerSecond: 200),
            Metrics(p95Ms: 50, operationsPerSecond: 80),
            Metrics(p95Ms: 10, operationsPerSecond: 250),
            Budgets());

        Assert.False(decision.WithinBudget);
        Assert.Equal("postgresql_granular_or_dedicated_projection", decision.RecommendedProfile);
        Assert.Equal(
            [
                "workload_failures_detected",
                "same_tenant_p95_exceeds_budget",
                "same_tenant_throughput_below_budget",
                "write_contention_ratio_exceeds_budget"
            ],
            decision.Violations);
    }

    [Fact]
    public void Options_RequireEnvironmentConnectionAndBoundEveryWorkloadInput()
    {
        var options = CanonicalMySqlBenchmarkOptions.Parse(
            [
                "--workers", "12",
                "--operations-per-worker", "40",
                "--warmup-operations", "0",
                "--seed-documents-per-tenant", "100",
                "--payload-bytes", "1024",
                "--max-same-tenant-p95-ms", "75.5",
                "--min-same-tenant-ops-per-second", "80",
                "--max-write-contention-ratio", "3.5",
                "--source-commit", "1822b6860e048f8abbc7ae9688d84632fe121952"
            ],
            name => name == "VYRAL_MYSQL_CONNECTION_STRING" ? "Server=localhost;Password=benchmark-secret" : null);

        Assert.Equal(12, options.Workers);
        Assert.Equal(40, options.OperationsPerWorker);
        Assert.Equal(0, options.WarmupOperations);
        Assert.Equal(100, options.SeedDocumentsPerTenant);
        Assert.Equal(1024, options.PayloadBytes);
        Assert.Equal(75.5, options.MaxSameTenantP95Ms);
        Assert.Equal(80, options.MinSameTenantOperationsPerSecond);
        Assert.Equal(3.5, options.MaxWriteContentionRatio);
        var serializedOptions = JsonSerializer.Serialize(options, CanonicalMySqlBenchmarkJson.Options);
        Assert.DoesNotContain("benchmark-secret", serializedOptions, StringComparison.Ordinal);
        Assert.DoesNotContain("connectionString", serializedOptions, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<InvalidOperationException>(() => CanonicalMySqlBenchmarkOptions.Parse(
            ["--workers", "65"],
            _ => "Server=localhost"));
        Assert.Throws<InvalidOperationException>(() => CanonicalMySqlBenchmarkOptions.Parse(
            ["--workres", "8"],
            _ => "Server=localhost"));
        Assert.Throws<InvalidOperationException>(() => CanonicalMySqlBenchmarkOptions.Parse([], _ => null));
    }

    [Fact]
    public void DecisionPolicy_RejectsBenchmarkThatRelaxesInnoDbCommitDurability()
    {
        var decision = CanonicalMySqlBenchmarkDecisionPolicy.Evaluate(
            Metrics(20, 100),
            Metrics(10, 200),
            Metrics(10, 200),
            Metrics(5, 400),
            Budgets(),
            new CanonicalMySqlBenchmarkDurability { InnoDbFlushLogAtTransactionCommit = 2 });

        Assert.False(decision.WithinBudget);
        Assert.Contains("durability_profile_not_flush_on_commit", decision.Violations);
    }

    [Fact]
    public void ReportAndFailureClassifier_ExposeNoConnectionOrExceptionMessage()
    {
        const string secret = "benchmark-secret-sentinel";
        var report = new CanonicalMySqlBenchmarkReport
        {
            SourceCommit = "1822b68",
            DatabaseVersion = "8.0.test",
            SameTenantWrites = Metrics(10, 100),
            DistinctTenantWrites = Metrics(5, 200),
            SameTenantReplays = Metrics(8, 120),
            DistinctTenantReplays = Metrics(4, 240)
        };
        var json = JsonSerializer.Serialize(report, CanonicalMySqlBenchmarkJson.Options);

        Assert.DoesNotContain("connection", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Equal(
            "validation_failure",
            CanonicalMySqlBenchmarkFailureClassifier.Classify(new InvalidOperationException(secret)));
    }

    private static CanonicalMySqlBenchmarkBudgets Budgets() =>
        new()
        {
            MaxSameTenantP95Ms = 100,
            MinSameTenantOperationsPerSecond = 50,
            MaxWriteContentionRatio = 4
        };

    private static CanonicalMySqlScenarioMetrics Metrics(
        double p95Ms,
        double operationsPerSecond,
        int failures = 0) =>
        new()
        {
            OperationsRequested = 100,
            OperationsCompleted = 100 - failures,
            OperationsFailed = failures,
            P95Ms = p95Ms,
            OperationsPerSecond = operationsPerSecond
        };
}
