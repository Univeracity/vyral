using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MySqlConnector;
using Vyral.Abstractions.Models;
using Vyral.MySql;

namespace Vyral.CanonicalStore.Benchmarks;

public static class CanonicalMySqlBenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

public sealed class CanonicalMySqlBenchmarkOptions
{
    private static readonly HashSet<string> KnownArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "workers",
        "operations-per-worker",
        "warmup-operations",
        "seed-documents-per-tenant",
        "payload-bytes",
        "timeout-seconds",
        "max-same-tenant-p95-ms",
        "min-same-tenant-ops-per-second",
        "max-write-contention-ratio",
        "source-commit",
        "output"
    };

    [JsonIgnore]
    public string ConnectionString { get; init; } = string.Empty;
    public int Workers { get; init; } = 8;
    public int OperationsPerWorker { get; init; } = 25;
    public int WarmupOperations { get; init; } = 8;
    public int SeedDocumentsPerTenant { get; init; } = 25;
    public int PayloadBytes { get; init; } = 256;
    public int TimeoutSeconds { get; init; } = 600;
    public double MaxSameTenantP95Ms { get; init; } = 100;
    public double MinSameTenantOperationsPerSecond { get; init; } = 50;
    public double MaxWriteContentionRatio { get; init; } = 4;
    public string SourceCommit { get; init; } = "unknown";
    [JsonIgnore]
    public string? OutputPath { get; init; }

    public static CanonicalMySqlBenchmarkOptions Parse(
        string[] args,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
                throw new InvalidOperationException("Canonical MySQL benchmark arguments must use --name value syntax.");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException($"Canonical MySQL benchmark argument '{argument}' requires a value.");
            var name = argument[2..];
            if (!KnownArguments.Contains(name))
                throw new InvalidOperationException($"Canonical MySQL benchmark argument '--{name}' is not supported.");
            if (!values.TryAdd(name, args[++index]))
                throw new InvalidOperationException($"Canonical MySQL benchmark argument '--{name}' was supplied more than once.");
        }

        var connectionString = getEnvironmentVariable("VYRAL_MYSQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("VYRAL_MYSQL_CONNECTION_STRING is required.");

        var options = new CanonicalMySqlBenchmarkOptions
        {
            ConnectionString = connectionString,
            Workers = Integer(values, "workers", 8, 1, 64),
            OperationsPerWorker = Integer(values, "operations-per-worker", 25, 1, 1_000),
            WarmupOperations = Integer(values, "warmup-operations", 8, 0, 1_000),
            SeedDocumentsPerTenant = Integer(values, "seed-documents-per-tenant", 25, 0, 1_000),
            PayloadBytes = Integer(values, "payload-bytes", 256, 0, 65_536),
            TimeoutSeconds = Integer(values, "timeout-seconds", 600, 10, 3_600),
            MaxSameTenantP95Ms = Number(values, "max-same-tenant-p95-ms", 100, 0.1, 60_000),
            MinSameTenantOperationsPerSecond = Number(values, "min-same-tenant-ops-per-second", 50, 0.01, 1_000_000),
            MaxWriteContentionRatio = Number(values, "max-write-contention-ratio", 4, 1, 1_000),
            SourceCommit = ValidatedSourceCommit(
                Optional(values, "source-commit")
                ?? getEnvironmentVariable("VYRAL_BENCHMARK_SOURCE_COMMIT")
                ?? "unknown"),
            OutputPath = Optional(values, "output")
        };
        return options;
    }

    private static int Integer(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(name, out var value)) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
            throw new InvalidOperationException($"--{name} must be between {minimum} and {maximum}.");
        return parsed;
    }

    private static double Number(
        IReadOnlyDictionary<string, string> values,
        string name,
        double fallback,
        double minimum,
        double maximum)
    {
        if (!values.TryGetValue(name, out var value)) return fallback;
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed) || parsed < minimum || parsed > maximum)
            throw new InvalidOperationException($"--{name} must be between {minimum} and {maximum}.");
        return parsed;
    }

    private static string? Optional(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string ValidatedSourceCommit(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (string.Equals(value, "unknown", StringComparison.Ordinal) ||
            (value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit)))
            return value;
        throw new InvalidOperationException("Benchmark source commit must be a hexadecimal Git commit id.");
    }
}

public sealed class CanonicalMySqlBenchmarkReport
{
    public string SchemaVersion { get; init; } = "canonical-mysql-contention/v1";
    public DateTime GeneratedAtUtc { get; init; }
    public string SourceCommit { get; init; } = string.Empty;
    public string DatabaseProduct { get; init; } = "mysql";
    public string DatabaseVersion { get; init; } = string.Empty;
    public string ServerDefaultTransactionIsolation { get; init; } = string.Empty;
    public string MutationTransactionIsolation { get; init; } = "READ-COMMITTED";
    public CanonicalMySqlBenchmarkDurability Durability { get; init; } = new();
    public string RuntimeVersion { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public int LogicalProcessorCount { get; init; }
    public CanonicalMySqlBenchmarkWorkload Workload { get; init; } = new();
    public CanonicalMySqlBenchmarkBudgets Budgets { get; init; } = new();
    public CanonicalMySqlScenarioMetrics SameTenantWrites { get; init; } = new();
    public CanonicalMySqlScenarioMetrics DistinctTenantWrites { get; init; } = new();
    public CanonicalMySqlScenarioMetrics SameTenantReplays { get; init; } = new();
    public CanonicalMySqlScenarioMetrics DistinctTenantReplays { get; init; } = new();
    public CanonicalMySqlBenchmarkDecision Decision { get; init; } = new();
}

public sealed class CanonicalMySqlBenchmarkWorkload
{
    public int Workers { get; init; }
    public int OperationsPerWorker { get; init; }
    public int WarmupOperations { get; init; }
    public int SeedDocumentsPerTenant { get; init; }
    public int PayloadBytes { get; init; }
    public string MutationShape { get; init; } = "one_document_plus_one_outbox_event";
    public string DurabilityShape { get; init; } = "provider_default_transaction_commit";
}

public sealed class CanonicalMySqlBenchmarkBudgets
{
    public double MaxSameTenantP95Ms { get; init; }
    public double MinSameTenantOperationsPerSecond { get; init; }
    public double MaxWriteContentionRatio { get; init; }
}

public sealed class CanonicalMySqlBenchmarkDurability
{
    public int InnoDbFlushLogAtTransactionCommit { get; init; }
    public int SyncBinlog { get; init; }
    public bool BinaryLogEnabled { get; init; }
}

public sealed class CanonicalMySqlScenarioMetrics
{
    public string Scenario { get; init; } = string.Empty;
    public int TenantCount { get; init; }
    public int OperationsRequested { get; init; }
    public int OperationsCompleted { get; init; }
    public int OperationsFailed { get; init; }
    public double WallClockMs { get; init; }
    public double OperationsPerSecond { get; init; }
    public double MeanMs { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }
    public long InitialMaxTenantStateBytes { get; init; }
    public long FinalMaxTenantStateBytes { get; init; }
    public long FinalTotalTenantStateBytes { get; init; }
    public IReadOnlyDictionary<string, int> FailureClasses { get; init; } = new Dictionary<string, int>();
}

public sealed class CanonicalMySqlBenchmarkDecision
{
    public bool WithinBudget { get; init; }
    public double WriteContentionRatio { get; init; }
    public double ReplayContentionRatio { get; init; }
    public string RecommendedProfile { get; init; } = string.Empty;
    public IReadOnlyList<string> Violations { get; init; } = Array.Empty<string>();
}

public static class CanonicalMySqlBenchmarkDecisionPolicy
{
    public static CanonicalMySqlBenchmarkDecision Evaluate(
        CanonicalMySqlScenarioMetrics sameTenantWrites,
        CanonicalMySqlScenarioMetrics distinctTenantWrites,
        CanonicalMySqlScenarioMetrics sameTenantReplays,
        CanonicalMySqlScenarioMetrics distinctTenantReplays,
        CanonicalMySqlBenchmarkBudgets budgets,
        CanonicalMySqlBenchmarkDurability? durability = null)
    {
        ArgumentNullException.ThrowIfNull(sameTenantWrites);
        ArgumentNullException.ThrowIfNull(distinctTenantWrites);
        ArgumentNullException.ThrowIfNull(sameTenantReplays);
        ArgumentNullException.ThrowIfNull(distinctTenantReplays);
        ArgumentNullException.ThrowIfNull(budgets);

        var writeRatio = Ratio(sameTenantWrites.P95Ms, distinctTenantWrites.P95Ms);
        var replayRatio = Ratio(sameTenantReplays.P95Ms, distinctTenantReplays.P95Ms);
        var violations = new List<string>();
        if (sameTenantWrites.OperationsFailed > 0 || distinctTenantWrites.OperationsFailed > 0 ||
            sameTenantReplays.OperationsFailed > 0 || distinctTenantReplays.OperationsFailed > 0)
            violations.Add("workload_failures_detected");
        if (durability is not null && durability.InnoDbFlushLogAtTransactionCommit != 1)
            violations.Add("durability_profile_not_flush_on_commit");
        if (sameTenantWrites.P95Ms > budgets.MaxSameTenantP95Ms)
            violations.Add("same_tenant_p95_exceeds_budget");
        if (sameTenantWrites.OperationsPerSecond < budgets.MinSameTenantOperationsPerSecond)
            violations.Add("same_tenant_throughput_below_budget");
        if (writeRatio > budgets.MaxWriteContentionRatio)
            violations.Add("write_contention_ratio_exceeds_budget");

        return new CanonicalMySqlBenchmarkDecision
        {
            WithinBudget = violations.Count == 0,
            WriteContentionRatio = Rounded(writeRatio),
            ReplayContentionRatio = Rounded(replayRatio),
            RecommendedProfile = violations.Count == 0
                ? "mysql_tenant_row"
                : "postgresql_granular_or_dedicated_projection",
            Violations = violations
        };
    }

    private static double Ratio(double numerator, double denominator)
    {
        if (denominator <= 0) return numerator <= 0 ? 1 : double.MaxValue;
        return numerator / denominator;
    }

    private static double Rounded(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

public static class CanonicalMySqlBenchmarkFailureClassifier
{
    public static string Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException) return "timeout_or_cancellation";
        if (exception is MySqlException { Number: 1205 }) return "lock_wait_timeout";
        if (exception is MySqlException { Number: 1213 }) return "deadlock";
        if (exception is MySqlException) return "provider_failure";
        if (exception is InvalidOperationException or ArgumentException) return "validation_failure";
        return "benchmark_failure";
    }
}

public static class CanonicalMySqlContentionBenchmark
{
    public static async Task<CanonicalMySqlBenchmarkReport> RunAsync(
        CanonicalMySqlBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("Canonical MySQL benchmark connection string is required.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var token = timeout.Token;
        var databaseName = "vyral_benchmark_" + Guid.NewGuid().ToString("N")[..16];
        var databaseVersion = string.Empty;
        var serverDefaultTransactionIsolation = string.Empty;
        var durability = new CanonicalMySqlBenchmarkDurability();

        try
        {
            var admin = new MySqlConnectionStringBuilder(options.ConnectionString) { Database = string.Empty };
            await using (var connection = new MySqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using (var version = connection.CreateCommand())
                {
                    version.CommandText = """
                        SELECT VERSION(), @@transaction_isolation,
                               @@innodb_flush_log_at_trx_commit, @@sync_binlog, @@log_bin;
                        """;
                    await using var reader = await version.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) throw new InvalidOperationException("MySQL did not return version diagnostics.");
                    databaseVersion = reader.GetString(0);
                    serverDefaultTransactionIsolation = reader.GetString(1);
                    durability = new CanonicalMySqlBenchmarkDurability
                    {
                        InnoDbFlushLogAtTransactionCommit = reader.GetInt32(2),
                        SyncBinlog = reader.GetInt32(3),
                        BinaryLogEnabled = reader.GetBoolean(4)
                    };
                }
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;";
                await create.ExecuteNonQueryAsync(token);
            }

            var databaseConnection = new MySqlConnectionStringBuilder(options.ConnectionString)
            {
                Database = databaseName
            }.ConnectionString;
            var store = new MySqlCanonicalStore(databaseConnection);
            await store.ApplyMigrationsAsync(Array.Empty<CanonicalMigration>(), token);
            await WarmAsync(store, options, token);

            var sameWriteTenants = new[] { "same-write" };
            var distinctWriteTenants = Enumerable.Range(0, options.Workers).Select(index => $"distinct-write-{index:D2}").ToArray();
            var sameReplayTenants = new[] { "same-replay" };
            var distinctReplayTenants = Enumerable.Range(0, options.Workers).Select(index => $"distinct-replay-{index:D2}").ToArray();

            await SeedAsync(store, sameWriteTenants.Concat(distinctWriteTenants), options, token);
            await SeedAsync(store, sameReplayTenants.Concat(distinctReplayTenants), options, token);
            var sameReplayRequest = ReplayRequest("same-replay", options.PayloadBytes);
            await store.CommitAsync(sameReplayRequest, token);
            var distinctReplayRequests = distinctReplayTenants.ToDictionary(
                tenant => tenant,
                tenant => ReplayRequest(tenant, options.PayloadBytes),
                StringComparer.Ordinal);
            foreach (var request in distinctReplayRequests.Values) await store.CommitAsync(request, token);

            var sameTenantWrites = await RunScenarioAsync(
                databaseConnection,
                "same_tenant_writes",
                sameWriteTenants,
                options,
                (worker, operation) => store.CommitAsync(WriteRequest("same-write", worker, operation, options.PayloadBytes), token),
                token);
            var distinctTenantWrites = await RunScenarioAsync(
                databaseConnection,
                "distinct_tenant_writes",
                distinctWriteTenants,
                options,
                (worker, operation) => store.CommitAsync(WriteRequest(distinctWriteTenants[worker], worker, operation, options.PayloadBytes), token),
                token);
            var sameTenantReplays = await RunScenarioAsync(
                databaseConnection,
                "same_tenant_idempotent_replays",
                sameReplayTenants,
                options,
                async (_, _) =>
                {
                    if (!(await store.CommitAsync(sameReplayRequest, token)).Replayed)
                        throw new InvalidOperationException("Expected an idempotent replay.");
                },
                token);
            var distinctTenantReplays = await RunScenarioAsync(
                databaseConnection,
                "distinct_tenant_idempotent_replays",
                distinctReplayTenants,
                options,
                async (worker, _) =>
                {
                    if (!(await store.CommitAsync(distinctReplayRequests[distinctReplayTenants[worker]], token)).Replayed)
                        throw new InvalidOperationException("Expected an idempotent replay.");
                },
                token);

            var budgets = new CanonicalMySqlBenchmarkBudgets
            {
                MaxSameTenantP95Ms = options.MaxSameTenantP95Ms,
                MinSameTenantOperationsPerSecond = options.MinSameTenantOperationsPerSecond,
                MaxWriteContentionRatio = options.MaxWriteContentionRatio
            };
            return new CanonicalMySqlBenchmarkReport
            {
                GeneratedAtUtc = DateTime.UtcNow,
                SourceCommit = options.SourceCommit,
                DatabaseVersion = databaseVersion,
                ServerDefaultTransactionIsolation = serverDefaultTransactionIsolation,
                Durability = durability,
                RuntimeVersion = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                LogicalProcessorCount = Environment.ProcessorCount,
                Workload = new CanonicalMySqlBenchmarkWorkload
                {
                    Workers = options.Workers,
                    OperationsPerWorker = options.OperationsPerWorker,
                    WarmupOperations = options.WarmupOperations,
                    SeedDocumentsPerTenant = options.SeedDocumentsPerTenant,
                    PayloadBytes = options.PayloadBytes
                },
                Budgets = budgets,
                SameTenantWrites = sameTenantWrites,
                DistinctTenantWrites = distinctTenantWrites,
                SameTenantReplays = sameTenantReplays,
                DistinctTenantReplays = distinctTenantReplays,
                Decision = CanonicalMySqlBenchmarkDecisionPolicy.Evaluate(
                    sameTenantWrites,
                    distinctTenantWrites,
                    sameTenantReplays,
                    distinctTenantReplays,
                    budgets,
                    durability)
            };
        }
        finally
        {
            var admin = new MySqlConnectionStringBuilder(options.ConnectionString) { Database = string.Empty };
            await using var connection = new MySqlConnection(admin.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`;";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task WarmAsync(
        MySqlCanonicalStore store,
        CanonicalMySqlBenchmarkOptions options,
        CancellationToken ct)
    {
        for (var index = 0; index < options.WarmupOperations; index++)
            await store.CommitAsync(WriteRequest($"warm-{index % Math.Max(1, options.Workers):D2}", 0, index, options.PayloadBytes), ct);
    }

    private static async Task SeedAsync(
        MySqlCanonicalStore store,
        IEnumerable<string> tenants,
        CanonicalMySqlBenchmarkOptions options,
        CancellationToken ct)
    {
        foreach (var tenant in tenants)
        {
            for (var offset = 0; offset < options.SeedDocumentsPerTenant; offset += CanonicalContractValidator.MaxTransactionMutations)
            {
                var count = Math.Min(CanonicalContractValidator.MaxTransactionMutations, options.SeedDocumentsPerTenant - offset);
                var request = new CanonicalTransactionRequest
                {
                    TenantId = tenant,
                    IdempotencyKey = $"seed:{offset:D6}",
                    Mutations = Enumerable.Range(offset, count).Select(index => new CanonicalMutation
                    {
                        Document = Document(tenant, $"seed-{index:D6}", options.PayloadBytes)
                    }).ToList()
                };
                await store.CommitAsync(request, ct);
            }
        }
    }

    private static async Task<CanonicalMySqlScenarioMetrics> RunScenarioAsync(
        string connectionString,
        string scenario,
        IReadOnlyList<string> tenants,
        CanonicalMySqlBenchmarkOptions options,
        Func<int, int, Task> operation,
        CancellationToken ct)
    {
        var initial = await ReadStateSizesAsync(connectionString, tenants, ct);
        var timings = new ConcurrentBag<double>();
        var failures = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, options.Workers).Select(worker => Task.Run(async () =>
        {
            await start.Task.WaitAsync(ct);
            for (var index = 0; index < options.OperationsPerWorker; index++)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    await operation(worker, index);
                    stopwatch.Stop();
                    timings.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    failures.AddOrUpdate(CanonicalMySqlBenchmarkFailureClassifier.Classify(exception), 1, (_, count) => count + 1);
                }
            }
        }, ct)).ToArray();

        var wallClock = Stopwatch.StartNew();
        start.SetResult();
        await Task.WhenAll(tasks);
        wallClock.Stop();
        var final = await ReadStateSizesAsync(connectionString, tenants, ct);
        var sorted = timings.OrderBy(value => value).ToArray();
        var requested = options.Workers * options.OperationsPerWorker;
        return new CanonicalMySqlScenarioMetrics
        {
            Scenario = scenario,
            TenantCount = tenants.Count,
            OperationsRequested = requested,
            OperationsCompleted = sorted.Length,
            OperationsFailed = failures.Values.Sum(),
            WallClockMs = Rounded(wallClock.Elapsed.TotalMilliseconds),
            OperationsPerSecond = Rounded(wallClock.Elapsed.TotalSeconds <= 0 ? 0 : sorted.Length / wallClock.Elapsed.TotalSeconds),
            MeanMs = Rounded(sorted.Length == 0 ? 0 : sorted.Average()),
            P50Ms = Rounded(Percentile(sorted, 0.50)),
            P95Ms = Rounded(Percentile(sorted, 0.95)),
            P99Ms = Rounded(Percentile(sorted, 0.99)),
            MaxMs = Rounded(sorted.Length == 0 ? 0 : sorted[^1]),
            InitialMaxTenantStateBytes = initial.Count == 0 ? 0 : initial.Max(),
            FinalMaxTenantStateBytes = final.Count == 0 ? 0 : final.Max(),
            FinalTotalTenantStateBytes = final.Sum(),
            FailureClasses = new SortedDictionary<string, int>(failures, StringComparer.Ordinal)
        };
    }

    private static async Task<IReadOnlyList<long>> ReadStateSizesAsync(
        string connectionString,
        IReadOnlyList<string> tenants,
        CancellationToken ct)
    {
        if (tenants.Count == 0) return Array.Empty<long>();
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var names = new List<string>(tenants.Count);
        for (var index = 0; index < tenants.Count; index++)
        {
            var name = $"@tenant_{index}";
            names.Add(name);
            command.Parameters.AddWithValue(name, tenants[index]);
        }
        command.CommandText = $"SELECT OCTET_LENGTH(state_json) FROM vyral_mysql_canonical_tenants WHERE tenant_id IN ({string.Join(",", names)});";
        var sizes = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) sizes.Add(reader.GetInt64(0));
        return sizes;
    }

    private static CanonicalTransactionRequest WriteRequest(string tenant, int worker, int operation, int payloadBytes) =>
        new()
        {
            TenantId = tenant,
            IdempotencyKey = $"write:{worker:D3}:{operation:D6}",
            Mutations =
            [
                new CanonicalMutation
                {
                    Document = Document(tenant, $"write-{worker:D3}-{operation:D6}", payloadBytes)
                }
            ],
            Outbox =
            [
                new CanonicalOutboxWrite
                {
                    Topic = "canonical.benchmark.changed",
                    Key = $"write-{worker:D3}-{operation:D6}",
                    Payload = new JsonObject { ["worker"] = worker, ["operation"] = operation }
                }
            ]
        };

    private static CanonicalTransactionRequest ReplayRequest(string tenant, int payloadBytes) =>
        new()
        {
            TenantId = tenant,
            IdempotencyKey = "replay-anchor",
            Mutations =
            [
                new CanonicalMutation
                {
                    Document = Document(tenant, "replay-anchor", payloadBytes)
                }
            ]
        };

    private static CanonicalDocument Document(string tenant, string id, int payloadBytes) =>
        new()
        {
            TenantId = tenant,
            DocumentType = "benchmark-item",
            Id = id,
            SchemaVersion = "v1",
            Data = new JsonObject
            {
                ["payload"] = new string('x', payloadBytes),
                ["id"] = id
            },
            Indexes = new Dictionary<string, string> { ["id"] = id }
        };

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static double Rounded(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
