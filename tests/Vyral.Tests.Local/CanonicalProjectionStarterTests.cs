using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;
using Vyral.CanonicalProjectionStarter;
using Vyral.Local;

namespace Vyral.Tests.Local;

public sealed class CanonicalProjectionStarterTests
{
    [Fact]
    public async Task Projection_RebuildsWithHashFenceAndAppliesOutboxExactlyOnce()
    {
        var canonical = CreateCanonicalStore();
        var projection = CreateProjection();
        await canonical.CommitAsync(CustomerUpsert("projection:create", "Ada", 1));
        await canonical.CommitAsync(new CanonicalTransactionRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "projection:other-topic",
            Outbox =
            [
                new CanonicalOutboxWrite
                {
                    Topic = "canonical.audit.recorded",
                    Key = "customer-1",
                    Payload = new JsonObject { ["kind"] = "customer-created" }
                }
            ]
        });

        var rebuilt = await projection.RebuildAsync(canonical, "tenant-a");
        var initial = Assert.IsType<CanonicalProjectionDocument>(
            await projection.GetAsync("tenant-a", "customer", "customer-1"));
        Assert.Equal(1, initial.Revision);
        Assert.Equal("Ada", initial.Data!["name"]!.GetValue<string>());
        Assert.Equal(rebuilt.SnapshotContentHash, await projection.GetRebuildFenceAsync("tenant-a"));

        var fenced = await projection.PumpOnceAsync(canonical, "tenant-a");
        Assert.Equal(2, fenced.Leased);
        Assert.Equal(2, fenced.Duplicate);

        await canonical.CommitAsync(CustomerUpsert("projection:update", "Ada Lovelace", 2));
        var ready = Assert.Single((await canonical.QueryOutboxAsync(new CanonicalOutboxQuery
        {
            TenantId = "tenant-a",
            State = CanonicalOutboxStates.Ready
        })).Items);
        Assert.False(await projection.ApplyAsync(canonical, ready));
        Assert.True(await projection.ApplyAsync(canonical, ready));

        var pumped = await projection.PumpOnceAsync(canonical, "tenant-a");
        Assert.Equal(1, pumped.Duplicate);
        var updated = Assert.IsType<CanonicalProjectionDocument>(
            await projection.GetAsync("tenant-a", "customer", "customer-1"));
        Assert.Equal(2, updated.Revision);
        Assert.Equal("Ada Lovelace", updated.Data!["name"]!.GetValue<string>());

        await canonical.CommitAsync(CustomerDelete());
        var deleted = await projection.PumpOnceAsync(canonical, "tenant-a");
        Assert.Equal(1, deleted.Applied);
        Assert.Null(await projection.GetAsync("tenant-a", "customer", "customer-1"));
    }

    [Fact]
    public async Task Projection_ParksAndExplicitlyReplaysInvalidEventWithFixedDiagnostic()
    {
        var canonical = CreateCanonicalStore();
        var projection = CreateProjection();
        var committed = await canonical.CommitAsync(new CanonicalTransactionRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "projection:invalid",
            Outbox =
            [
                new CanonicalOutboxWrite
                {
                    Topic = "canonical.document.changed",
                    Key = "invalid",
                    Payload = new JsonObject { ["documentType"] = "customer" },
                    MaxDeliveryAttempts = 1
                }
            ]
        });

        var first = await projection.PumpOnceAsync(canonical, "tenant-a");
        Assert.Equal(1, first.Released);
        var dead = Assert.Single((await canonical.QueryOutboxAsync(new CanonicalOutboxQuery
        {
            TenantId = "tenant-a",
            State = CanonicalOutboxStates.DeadLetter
        })).Items);
        Assert.Equal("projection_failure", dead.LastError);
        Assert.DoesNotContain("payload", dead.LastError, StringComparison.OrdinalIgnoreCase);

        await canonical.ReplayOutboxAsync(new CanonicalOutboxReplayRequest
        {
            TenantId = "tenant-a",
            EventId = Assert.Single(committed.Outbox).Id,
            ResetDeliveryCount = true
        });
        var replay = await projection.PumpOnceAsync(canonical, "tenant-a");
        Assert.Equal(1, replay.Released);
        Assert.Single((await canonical.QueryOutboxAsync(new CanonicalOutboxQuery
        {
            TenantId = "tenant-a",
            State = CanonicalOutboxStates.DeadLetter
        })).Items);
    }

    private static SqliteCanonicalStore CreateCanonicalStore() =>
        new(Path.Combine(Path.GetTempPath(), $"vyral-canonical-projection-source-{Guid.NewGuid():N}.sqlite"));

    private static SqliteCanonicalProjection CreateProjection() =>
        new(new SqliteCanonicalProjectionOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-projection-view-{Guid.NewGuid():N}.sqlite")
        });

    private static CanonicalTransactionRequest CustomerUpsert(string idempotencyKey, string name, long revision) =>
        new()
        {
            TenantId = "tenant-a",
            IdempotencyKey = idempotencyKey,
            Mutations =
            [
                new CanonicalMutation
                {
                    Document = new CanonicalDocument
                    {
                        TenantId = "tenant-a",
                        DocumentType = "customer",
                        Id = "customer-1",
                        SchemaVersion = "v1",
                        Data = new JsonObject { ["name"] = name },
                        Indexes = new Dictionary<string, string> { ["name"] = name }
                    },
                    Precondition = revision > 1
                        ? new CanonicalWritePrecondition { ExpectedRevision = revision - 1, MustExist = true }
                        : null
                }
            ],
            Fences = revision == 1
                ?
                [
                    new CanonicalFenceMutation
                    {
                        Name = "customer-email",
                        Value = "ada@example.test",
                        OwnerDocumentType = "customer",
                        OwnerDocumentId = "customer-1"
                    }
                ]
                : [],
            Outbox = [ChangedEvent("customer-1", revision)]
        };

    private static CanonicalTransactionRequest CustomerDelete() =>
        new()
        {
            TenantId = "tenant-a",
            IdempotencyKey = "projection:delete",
            Mutations =
            [
                new CanonicalMutation
                {
                    Operation = CanonicalMutationOperations.Delete,
                    DocumentType = "customer",
                    Id = "customer-1",
                    Precondition = new CanonicalWritePrecondition { ExpectedRevision = 2, MustExist = true }
                }
            ],
            Outbox = [ChangedEvent("customer-1", 3)]
        };

    private static CanonicalOutboxWrite ChangedEvent(string id, long revision) =>
        new()
        {
            Topic = "canonical.document.changed",
            Key = id,
            Payload = new JsonObject
            {
                ["documentType"] = "customer",
                ["documentId"] = id,
                ["revision"] = revision
            }
        };
}
