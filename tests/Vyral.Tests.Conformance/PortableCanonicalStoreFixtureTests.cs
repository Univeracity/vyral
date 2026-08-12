using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Conformance;

public sealed class PortableCanonicalStoreFixtureTests
{
    private const string ScenarioId = "canonical.strong-profile.v1";
    private const string ManifestResource =
        "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource =
        "Vyral.Tests.Conformance.runtime-v1-canonical-strong-profile.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void CanonicalArchiveRejectsExcessiveChunkCountsBeforeHashing()
    {
        var archive = new CanonicalTenantArchive
        {
            TenantId = "tenant-a",
            ExportedAtUtc = DateTime.UnixEpoch,
            SnapshotContentHash = "sha256:not-evaluated",
            ContentHash = "sha256:not-evaluated",
            Chunks = Enumerable.Range(0, CanonicalTenantArchive.MaxChunks + 1)
                .Select(index => new CanonicalTenantArchiveChunk
                {
                    Index = index,
                    Content = [0x01],
                    Length = 1,
                    ContentHash = "sha256:not-evaluated"
                })
                .ToList()
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            CanonicalTenantArchiveCodec.Read(
                new CanonicalArchiveRestoreRequest { Archive = archive }));

        Assert.Contains("chunk limit", error.Message);
    }

    [Fact]
    public async Task StrongCanonicalStoreMatchesThePortableFixture()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("id").GetString() == ScenarioId);
        Assert.Equal("stateful", descriptor.GetProperty("kind").GetString());
        Assert.Equal(
            "vyral.runtime.canonical.v1",
            descriptor.GetProperty("profile").GetString());

        var scenarioBytes = ReadResource(ScenarioResource);
        var actualDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(scenarioBytes));
        Assert.Equal(
            descriptor.GetProperty("sha256").GetString(),
            actualDigest);

        var path = Path.Combine(
            Path.GetTempPath(),
            $"vyral-portable-canonical-{Guid.NewGuid():N}.sqlite");
        var runner = new FixtureRunner(new SqliteCanonicalStore(path));
        try
        {
            using var scenario = JsonDocument.Parse(scenarioBytes);
            foreach (
                var step in scenario.RootElement
                    .GetProperty("steps")
                    .EnumerateArray())
            {
                var actual = await runner.ExecuteAsync(
                    step.GetProperty("operation").GetString()!,
                    step.GetProperty("arguments"));
                var expected = JsonNode.Parse(
                    step.GetProperty("expect")
                        .GetProperty("value")
                        .GetRawText());
                Assert.True(
                    JsonNode.DeepEquals(expected, actual),
                    $"Canonical step " +
                    $"'{step.GetProperty("id").GetString()}' produced " +
                    $"{actual.ToJsonString()}, expected " +
                    $"{expected?.ToJsonString() ?? "null"}.");
            }
        }
        finally
        {
            SqliteConnectionPoolClear();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (File.Exists(path + "-wal"))
            {
                File.Delete(path + "-wal");
            }
            if (File.Exists(path + "-shm"))
            {
                File.Delete(path + "-shm");
            }
        }
    }

    private static void SqliteConnectionPoolClear()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private sealed class FixtureRunner(SqliteCanonicalStore store)
    {
        private CanonicalTransactionRequest? _seedRequest;

        public Task<JsonObject> ExecuteAsync(
            string operation,
            JsonElement arguments) =>
            operation switch
            {
                "canonical.commit-replay" =>
                    CommitReplayAsync(arguments),
                "canonical.conflicts-and-tombstone" =>
                    ConflictsAndTombstoneAsync(arguments),
                "canonical.outbox-lifecycle" =>
                    OutboxLifecycleAsync(arguments),
                "canonical.restore-roundtrip" =>
                    RestoreRoundtripAsync(arguments),
                "canonical.migrations-query-isolation" =>
                    MigrationsQueryIsolationAsync(arguments),
                "canonical.snapshot-codec" =>
                    SnapshotCodecAsync(arguments),
                _ => throw new InvalidOperationException(
                    $"Unsupported canonical fixture operation " +
                    $"'{operation}'.")
            };

        private static Task<JsonObject> SnapshotCodecAsync(
            JsonElement arguments)
        {
            var source = Deserialize<CanonicalTenantSnapshot>(
                arguments.GetProperty("snapshot"));
            var snapshotHash = CanonicalSnapshotHasher.Compute(source);
            source.ContentHash = snapshotHash;
            var archive = CanonicalTenantArchiveCodec.Create(
                source,
                arguments.GetProperty("chunkBytes").GetInt32());
            var roundTrip = CanonicalTenantArchiveCodec.Read(
                new CanonicalArchiveRestoreRequest
                {
                    Archive = archive,
                    ExpectedContentHash = archive.ContentHash
                });
            return Task.FromResult(
                new JsonObject
                {
                    ["snapshotHash"] = snapshotHash,
                    ["archiveHash"] = archive.ContentHash,
                    ["chunkCount"] = archive.Chunks.Count,
                    ["chunkHashes"] = StringsInOrder(
                        archive.Chunks.Select(
                            item => item.ContentHash)),
                    ["roundTripHash"] =
                        CanonicalSnapshotHasher.Compute(roundTrip)
                });
        }

        private async Task<JsonObject> CommitReplayAsync(
            JsonElement arguments)
        {
            var request = Deserialize<CanonicalTransactionRequest>(
                arguments.GetProperty("request"));
            _seedRequest = request;
            var first = await store.CommitAsync(request);
            var replay = await store.CommitAsync(request);
            var query = await store.QueryDocumentsAsync(
                new CanonicalDocumentQuery
                {
                    TenantId = request.TenantId,
                    DocumentType = "author",
                    Indexes =
                    {
                        ["email"] = "ada@example.test"
                    }
                });
            var snapshot = await store.ExportTenantAsync(request.TenantId);
            var document = Assert.Single(first.Documents);
            var outbox = Assert.Single(first.Outbox);
            return new JsonObject
            {
                ["transactionId"] = first.TransactionId,
                ["firstReplayed"] = first.Replayed,
                ["replayReplayed"] = replay.Replayed,
                ["revision"] = document.Revision,
                ["etag"] = document.Etag,
                ["outboxId"] = outbox.Id,
                ["queryIds"] = Strings(
                    query.Items.Select(item => item.Id)),
                ["snapshotCounts"] = new JsonObject
                {
                    ["documents"] = snapshot.Documents.Count,
                    ["revisions"] = snapshot.Revisions.Count,
                    ["fences"] = snapshot.Fences.Count,
                    ["outbox"] = snapshot.Outbox.Count,
                    ["transactions"] = snapshot.Transactions.Count
                }
            };
        }

        private async Task<JsonObject> ConflictsAndTombstoneAsync(
            JsonElement arguments)
        {
            var conflict = Deserialize<CanonicalTransactionRequest>(
                arguments.GetProperty("conflictingFenceRequest"));
            var fenceConflict = await RejectsAsync(
                () => store.CommitAsync(conflict));
            var conflictingDocument = await store.GetDocumentAsync(
                "tenant-a",
                "author",
                "a-2",
                includeDeleted: true);

            var updated = Assert.Single(
                (await store.CommitAsync(
                    Deserialize<CanonicalTransactionRequest>(
                        arguments.GetProperty("updateRequest"))))
                .Documents);
            var stale = await RejectsAsync(
                () => store.CommitAsync(
                    Deserialize<CanonicalTransactionRequest>(
                        arguments.GetProperty("staleRequest"))));
            var deleted = Assert.Single(
                (await store.CommitAsync(
                    Deserialize<CanonicalTransactionRequest>(
                        arguments.GetProperty("deleteRequest"))))
                .Documents);
            var normal = await store.GetDocumentAsync(
                "tenant-a",
                "author",
                "a-1");
            var tombstone = await store.GetDocumentAsync(
                "tenant-a",
                "author",
                "a-1",
                includeDeleted: true);
            var revisions = await store.GetRevisionsAsync(
                "tenant-a",
                "author",
                "a-1");
            return new JsonObject
            {
                ["fenceConflictRejected"] = fenceConflict,
                ["conflictingDocumentAbsent"] =
                    conflictingDocument is null,
                ["updatedRevision"] = updated.Revision,
                ["staleRevisionRejected"] = stale,
                ["deletedRevision"] = deleted.Revision,
                ["normalReadMissing"] = normal is null,
                ["tombstoneDeleted"] = tombstone?.Deleted == true,
                ["revisionOrder"] = Longs(
                    revisions.Select(item => item.Revision)),
                ["revisionOperations"] = StringsInOrder(
                    revisions.Select(item => item.Operation))
            };
        }

        private async Task<JsonObject> OutboxLifecycleAsync(
            JsonElement arguments)
        {
            var tenantId =
                arguments.GetProperty("tenantId").GetString()!;
            var consumerId =
                arguments.GetProperty("consumerId").GetString()!;
            var notBefore = arguments
                .GetProperty("releaseNotBeforeUtc")
                .GetDateTime()
                .ToUniversalTime();
            var first = Assert.Single(
                await store.LeaseOutboxAsync(
                    new CanonicalOutboxLeaseRequest
                    {
                        TenantId = tenantId,
                        ConsumerId = consumerId,
                        LeaseSeconds = 60
                    }));
            var renewal = await store.RenewOutboxLeaseAsync(
                new CanonicalOutboxLeaseRenewalRequest
                {
                    TenantId = tenantId,
                    EventId = first.Event.Id,
                    LeaseToken = first.LeaseToken,
                    LeaseSeconds = 120
                });
            await store.NackOutboxAsync(
                new CanonicalOutboxNackRequest
                {
                    TenantId = tenantId,
                    EventId = first.Event.Id,
                    LeaseToken = first.LeaseToken,
                    NotBeforeUtc = notBefore,
                    Error = "portable retry"
                });
            var second = Assert.Single(
                await store.LeaseOutboxAsync(
                    new CanonicalOutboxLeaseRequest
                    {
                        TenantId = tenantId,
                        ConsumerId = consumerId,
                        LeaseSeconds = 60
                    }));
            await store.AcknowledgeOutboxAsync(
                tenantId,
                second.Event.Id,
                second.LeaseToken);
            await store.AcknowledgeOutboxAsync(
                tenantId,
                second.Event.Id,
                second.LeaseToken);
            var delivered = await store.QueryOutboxAsync(
                new CanonicalOutboxQuery
                {
                    TenantId = tenantId,
                    State = CanonicalOutboxStates.Delivered
                });
            var afterAck = await store.LeaseOutboxAsync(
                new CanonicalOutboxLeaseRequest
                {
                    TenantId = tenantId,
                    ConsumerId = consumerId,
                    LeaseSeconds = 60
                });
            return new JsonObject
            {
                ["firstDeliveryCount"] = first.Event.DeliveryCount,
                ["secondDeliveryCount"] = second.Event.DeliveryCount,
                ["renewed"] =
                    renewal.ExpiresAtUtc > first.ExpiresAtUtc,
                ["deliveredCount"] = delivered.Items.Count,
                ["leaseAfterAckCount"] = afterAck.Count
            };
        }

        private async Task<JsonObject> RestoreRoundtripAsync(
            JsonElement arguments)
        {
            var archive = await store.ExportTenantArchiveAsync(
                "tenant-a",
                arguments.GetProperty("chunkBytes").GetInt32());
            await store.CommitAsync(
                Deserialize<CanonicalTransactionRequest>(
                    arguments.GetProperty("additionalRequest")));
            await store.RestoreTenantArchiveAsync(
                new CanonicalArchiveRestoreRequest
                {
                    Archive = archive,
                    ExpectedContentHash = archive.ContentHash
                });
            var original = await store.GetDocumentAsync(
                "tenant-a",
                "author",
                "a-1",
                includeDeleted: true);
            var additional = await store.GetDocumentAsync(
                "tenant-a",
                "author",
                "a-2",
                includeDeleted: true);
            var replay = await store.CommitAsync(
                _seedRequest
                ?? throw new InvalidOperationException(
                    "Canonical seed request is unavailable."));

            var corrupt = Clone(archive);
            corrupt.Chunks[0].Content[0] ^= 0x01;
            var corruptionRejected = await RejectsAsync(
                () => store.RestoreTenantArchiveAsync(
                    new CanonicalArchiveRestoreRequest
                    {
                        Archive = corrupt,
                        ExpectedContentHash = corrupt.ContentHash
                    }));
            return new JsonObject
            {
                ["archiveHasMultipleChunks"] =
                    archive.Chunks.Count > 1,
                ["chunkIndexesContiguous"] = archive.Chunks
                    .Select((chunk, index) => chunk.Index == index)
                    .All(value => value),
                ["originalTombstoneRestored"] =
                    original?.Deleted == true,
                ["additionalDocumentAbsent"] = additional is null,
                ["idempotencyReceiptRestored"] = replay.Replayed,
                ["corruptionRejected"] = corruptionRejected
            };
        }

        private async Task<JsonObject> MigrationsQueryIsolationAsync(
            JsonElement arguments)
        {
            var migration = Deserialize<CanonicalMigration>(
                arguments.GetProperty("migration"));
            await store.ApplyMigrationsAsync([migration]);
            await store.ApplyMigrationsAsync([migration]);
            var checksumConflict = await RejectsAsync(
                () => store.ApplyMigrationsAsync(
                    [
                        new CanonicalMigration
                        {
                            Namespace = migration.Namespace,
                            Id = migration.Id,
                            Checksum = "sha256:different"
                        }
                    ]));
            await store.ApplyMigrationsAsync(
                [
                    new CanonicalMigration
                    {
                        Namespace = "portable-other",
                        Id = migration.Id,
                        Checksum = "sha256:two"
                    }
                ]);

            foreach (
                var (id, rank) in new[]
                {
                    ("e-1", "020"),
                    ("e-2", "010"),
                    ("e-3", "030")
                })
            {
                await store.CommitAsync(
                    Upsert(
                        "tenant-a",
                        "rank-" + id,
                        "review",
                        id,
                        id,
                        new Dictionary<string, string>
                        {
                            ["rank"] = rank
                        }));
            }
            await store.CommitAsync(
                Upsert(
                    "tenant-a",
                    "isolation-a",
                    "entity",
                    "same",
                    "A"));
            await store.CommitAsync(
                Upsert(
                    "tenant-b",
                    "isolation-b",
                    "entity",
                    "same",
                    "B"));

            var first = await PageAsync(null);
            var second = await PageAsync(first.ContinuationToken);
            var third = await PageAsync(second.ContinuationToken);
            var tenantA = await store.GetDocumentAsync(
                "tenant-a",
                "entity",
                "same");
            var tenantB = await store.GetDocumentAsync(
                "tenant-b",
                "entity",
                "same");
            return new JsonObject
            {
                ["migrationCount"] =
                    (await store.ListMigrationsAsync()).Count,
                ["checksumConflictRejected"] = checksumConflict,
                ["pageIds"] = StringsInOrder(
                    new[]
                    {
                        Assert.Single(first.Items).Id,
                        Assert.Single(second.Items).Id,
                        Assert.Single(third.Items).Id
                    }),
                ["continuations"] = new JsonArray(
                    JsonValue.Create(
                        first.ContinuationToken is not null),
                    JsonValue.Create(
                        second.ContinuationToken is not null),
                    JsonValue.Create(
                        third.ContinuationToken is not null)),
                ["tenantValues"] = StringsInOrder(
                    new[]
                    {
                        tenantA!.Data!["value"]!.GetValue<string>(),
                        tenantB!.Data!["value"]!.GetValue<string>()
                    })
            };
        }

        private Task<CanonicalDocumentQueryResult> PageAsync(
            string? continuationToken) =>
            store.QueryDocumentsAsync(
                new CanonicalDocumentQuery
                {
                    TenantId = "tenant-a",
                    DocumentType = "review",
                    OrderByIndex = "rank",
                    OrderDirection =
                        CanonicalDocumentOrderDirections.Descending,
                    IndexRange = new CanonicalDocumentIndexRange
                    {
                        Name = "rank",
                        GreaterThanOrEqual = "010",
                        LessThanOrEqual = "030"
                    },
                    Limit = 1,
                    ContinuationToken = continuationToken
                });

        private static CanonicalTransactionRequest Upsert(
            string tenantId,
            string idempotencyKey,
            string documentType,
            string id,
            string value,
            Dictionary<string, string>? indexes = null) =>
            new()
            {
                TenantId = tenantId,
                IdempotencyKey = idempotencyKey,
                Mutations =
                [
                    new CanonicalMutation
                    {
                        Document = new CanonicalDocument
                        {
                            TenantId = tenantId,
                            DocumentType = documentType,
                            Id = id,
                            SchemaVersion = "v1",
                            Data = new JsonObject
                            {
                                ["value"] = value
                            },
                            Indexes = indexes ?? []
                        }
                    }
                ]
            };

        private static async Task<bool> RejectsAsync(Func<Task> action)
        {
            try
            {
                await action();
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    private static T Deserialize<T>(JsonElement value) =>
        value.Deserialize<T>(JsonOptions)
        ?? throw new InvalidOperationException(
            "Portable canonical fixture value is invalid.");

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions)
        ?? throw new InvalidOperationException(
            "Portable canonical fixture clone failed.");

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => JsonValue.Create(value))
            .ToArray());

    private static JsonArray StringsInOrder(
        IEnumerable<string> values) =>
        new(values
            .Select(value => JsonValue.Create(value))
            .ToArray());

    private static JsonArray Longs(IEnumerable<long> values) =>
        new(values
            .Select(value => JsonValue.Create(value))
            .ToArray());

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableCanonicalStoreFixtureTests)
            .Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
