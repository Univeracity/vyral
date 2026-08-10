using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

/// <summary>
/// Executable contract for the strong CanonicalStore profile. Provider implementations must run
/// this suite; weaker stores should expose a different interface rather than partially satisfy it.
/// </summary>
public abstract class CanonicalStoreConformanceTests
{
    protected abstract Task<ICanonicalStore> CreateStoreAsync();

    protected async Task RunCanonicalStore_CommitsDocumentFenceAndOutboxAtomically()
    {
        await WithStoreAsync(async store =>
        {
            var request = CreateUpsert("tenant-a", "create-1", "author", "a-1", "Ada", "ada@example.test", includeOutbox: true);
            request.Fences.Add(new CanonicalFenceMutation
            {
                Name = "author-email",
                Value = "ada@example.test",
                OwnerDocumentType = "author",
                OwnerDocumentId = "a-1"
            });

            var result = await store.CommitAsync(request);

            var document = Assert.Single(result.Documents);
            Assert.Equal(1, document.Revision);
            Assert.Equal("rev:1", document.Etag);
            Assert.Single(result.Outbox);
            Assert.Equal(result.TransactionId, result.Outbox[0].TransactionId);
            var queried = await store.QueryDocumentsAsync(new CanonicalDocumentQuery
            {
                TenantId = "tenant-a",
                DocumentType = "author",
                Indexes = new Dictionary<string, string> { ["email"] = "ada@example.test" }
            });
            Assert.Equal("a-1", Assert.Single(queried.Items).Id);

            var exported = await store.ExportTenantAsync("tenant-a");
            Assert.Single(exported.Fences);
            Assert.Single(exported.Outbox);
            Assert.Single(exported.Transactions);
        });
    }

    protected async Task RunCanonicalStore_ReplaysIdempotentCommitAndRejectsDifferentRequest()
    {
        await WithStoreAsync(async store =>
        {
            var request = CreateUpsert("tenant-a", "request-1", "claim", "c-1", "First", "first@example.test", includeOutbox: true);
            var first = await store.CommitAsync(request);
            var replay = await store.CommitAsync(request);

            Assert.False(first.Replayed);
            Assert.True(replay.Replayed);
            Assert.Equal(first.TransactionId, replay.TransactionId);
            Assert.Equal(first.CommittedAtUtc, replay.CommittedAtUtc);
            Assert.Equal(first.Outbox.Single().Id, replay.Outbox.Single().Id);
            Assert.Single(await store.GetRevisionsAsync("tenant-a", "claim", "c-1"));

            var changed = CreateUpsert("tenant-a", "request-1", "claim", "c-1", "Changed", "changed@example.test");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(changed));
        });
    }

    protected async Task RunCanonicalStore_ConcurrentlyReplaysTheSameIdempotentCommit()
    {
        await WithStoreAsync(async store =>
        {
            var request = CreateUpsert("tenant-a", "parallel-request", "entity", "e-1", "Concurrent", "concurrent@example.test", includeOutbox: true);
            var results = await Task.WhenAll(store.CommitAsync(request), store.CommitAsync(request));

            Assert.Single(results, result => !result.Replayed);
            Assert.Single(results, result => result.Replayed);
            Assert.Equal(results[0].TransactionId, results[1].TransactionId);
            Assert.Single(await store.GetRevisionsAsync("tenant-a", "entity", "e-1"));
            var snapshot = await store.ExportTenantAsync("tenant-a");
            Assert.Single(snapshot.Outbox);
        });
    }

    protected async Task RunCanonicalStore_EnforcesConditionalWritesAndFenceAtomicity()
    {
        await WithStoreAsync(async store =>
        {
            var first = CreateUpsert("tenant-a", "author-1", "author", "a-1", "Ada", "ada@example.test");
            first.Fences.Add(new CanonicalFenceMutation { Name = "author-email", Value = "ada@example.test", OwnerDocumentType = "author", OwnerDocumentId = "a-1" });
            await store.CommitAsync(first);

            var rejected = CreateUpsert("tenant-a", "author-2", "author", "a-2", "Grace", "ada@example.test");
            rejected.Fences.Add(new CanonicalFenceMutation { Name = "author-email", Value = "ada@example.test", OwnerDocumentType = "author", OwnerDocumentId = "a-2" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(rejected));
            Assert.Null(await store.GetDocumentAsync("tenant-a", "author", "a-2"));

            var update = CreateUpsert("tenant-a", "author-3", "author", "a-1", "Ada Lovelace", "ada@example.test");
            update.Mutations[0].Precondition = new CanonicalWritePrecondition { ExpectedRevision = 1, MustExist = true };
            var updated = Assert.Single((await store.CommitAsync(update)).Documents);
            Assert.Equal(2, updated.Revision);

            var stale = CreateUpsert("tenant-a", "author-4", "author", "a-1", "Stale", "ada@example.test");
            stale.Mutations[0].Precondition = new CanonicalWritePrecondition { ExpectedRevision = 1 };
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(stale));
            Assert.Equal("Ada Lovelace", (await store.GetDocumentAsync("tenant-a", "author", "a-1"))!.Data!["name"]!.GetValue<string>());
        });
    }

    protected async Task RunCanonicalStore_RetainsRevisionsAndTombstones()
    {
        await WithStoreAsync(async store =>
        {
            await store.CommitAsync(CreateUpsert("tenant-a", "claim-1", "claim", "c-1", "Draft", "draft@example.test"));
            var delete = new CanonicalTransactionRequest
            {
                TenantId = "tenant-a",
                IdempotencyKey = "claim-delete",
                Mutations = new List<CanonicalMutation>
                {
                    new()
                    {
                        Operation = CanonicalMutationOperations.Delete,
                        DocumentType = "claim",
                        Id = "c-1",
                        Precondition = new CanonicalWritePrecondition { ExpectedRevision = 1, MustExist = true }
                    }
                }
            };
            var tombstone = Assert.Single((await store.CommitAsync(delete)).Documents);
            Assert.True(tombstone.Deleted);
            Assert.Equal(2, tombstone.Revision);
            Assert.Null(await store.GetDocumentAsync("tenant-a", "claim", "c-1"));
            Assert.True((await store.GetDocumentAsync("tenant-a", "claim", "c-1", includeDeleted: true))!.Deleted);
            var revisions = await store.GetRevisionsAsync("tenant-a", "claim", "c-1");
            Assert.Equal(new long[] { 2, 1 }, revisions.Select(item => item.Revision));
            Assert.Equal(CanonicalMutationOperations.Delete, revisions[0].Operation);
        });
    }

    protected async Task RunCanonicalStore_LeasesAcknowledgesAndReleasesOutbox()
    {
        await WithStoreAsync(async store =>
        {
            await store.CommitAsync(CreateUpsert("tenant-a", "event-1", "projection", "p-1", "Pending", "pending@example.test", includeOutbox: true));
            var firstLease = Assert.Single(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest { TenantId = "tenant-a", ConsumerId = "projector", LeaseSeconds = 60 }));
            Assert.Equal(1, firstLease.Event.DeliveryCount);
            var renewal = await store.RenewOutboxLeaseAsync(new CanonicalOutboxLeaseRenewalRequest
            {
                TenantId = "tenant-a", EventId = firstLease.Event.Id, LeaseToken = firstLease.LeaseToken, LeaseSeconds = 120
            });
            Assert.True(renewal.ExpiresAtUtc > firstLease.ExpiresAtUtc);
            await store.NackOutboxAsync(new CanonicalOutboxNackRequest { TenantId = "tenant-a", EventId = firstLease.Event.Id, LeaseToken = firstLease.LeaseToken, Error = "retry", NotBeforeUtc = DateTime.UtcNow });

            var secondLease = Assert.Single(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest { TenantId = "tenant-a", ConsumerId = "projector", LeaseSeconds = 60 }));
            Assert.Equal(2, secondLease.Event.DeliveryCount);
            await store.AcknowledgeOutboxAsync("tenant-a", secondLease.Event.Id, secondLease.LeaseToken);
            await store.AcknowledgeOutboxAsync("tenant-a", secondLease.Event.Id, secondLease.LeaseToken);
            Assert.Empty(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest { TenantId = "tenant-a", ConsumerId = "projector", LeaseSeconds = 60 }));
        });
    }

    protected async Task RunCanonicalStore_ParksAndReplaysDeadLetteredOutbox()
    {
        await WithStoreAsync(async store =>
        {
            var request = CreateUpsert("tenant-a", "dead-letter-1", "projection", "p-1", "Pending", "pending@example.test", includeOutbox: true);
            request.Outbox[0].MaxDeliveryAttempts = 1;
            await store.CommitAsync(request);
            var lease = Assert.Single(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest { TenantId = "tenant-a", ConsumerId = "projector", LeaseSeconds = 60 }));
            await store.NackOutboxAsync(new CanonicalOutboxNackRequest { TenantId = "tenant-a", EventId = lease.Event.Id, LeaseToken = lease.LeaseToken, Error = "permanent failure" });

            var dead = await store.QueryOutboxAsync(new CanonicalOutboxQuery { TenantId = "tenant-a", State = CanonicalOutboxStates.DeadLetter });
            var parked = Assert.Single(dead.Items);
            Assert.NotNull(parked.DeadLetteredAtUtc);
            Assert.Equal("permanent failure", parked.LastError);
            Assert.Empty(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest { TenantId = "tenant-a", ConsumerId = "projector", LeaseSeconds = 60 }));

            await store.ReplayOutboxAsync(new CanonicalOutboxReplayRequest { TenantId = "tenant-a", EventId = parked.Id, ResetDeliveryCount = true });
            var replayed = Assert.Single(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest { TenantId = "tenant-a", ConsumerId = "projector", LeaseSeconds = 60 }));
            Assert.Equal(1, replayed.Event.DeliveryCount);
        });
    }

    protected async Task RunCanonicalStore_PreservesHashVerifiedActiveLeaseSnapshot()
    {
        await WithStoreAsync(async store =>
        {
            await store.CommitAsync(CreateUpsert("tenant-a", "snapshot-lease", "projection", "p-1", "Pending", "pending@example.test", includeOutbox: true));
            _ = Assert.Single(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest { TenantId = "tenant-a", ConsumerId = "projector", LeaseSeconds = 60 }));
            var snapshot = await store.ExportTenantAsync("tenant-a");
            var hash = snapshot.ContentHash;
            Assert.NotNull(Assert.Single(snapshot.Outbox).LeaseOwner);

            await store.RestoreTenantAsync(new CanonicalRestoreRequest { Snapshot = snapshot, ExpectedContentHash = hash });
            await store.RestoreTenantAsync(new CanonicalRestoreRequest { Snapshot = snapshot, ExpectedContentHash = hash });
            Assert.Equal(hash, snapshot.ContentHash);
            Assert.NotNull(snapshot.Outbox[0].LeaseOwner);
        });
    }

    protected async Task RunCanonicalStore_RoundTripsHashVerifiedChunkedTenantArchive()
    {
        await WithStoreAsync(async store =>
        {
            await store.CommitAsync(CreateUpsert("tenant-a", "archive-before", "entity", "e-1", "Before", "before@example.test", includeOutbox: true));
            var archive = await store.ExportTenantArchiveAsync("tenant-a", chunkBytes: 128);
            Assert.True(archive.Chunks.Count > 1);
            Assert.All(archive.Chunks, (chunk, index) => Assert.Equal(index, chunk.Index));

            await store.CommitAsync(CreateUpsert("tenant-a", "archive-after", "entity", "e-2", "After", "after@example.test"));
            await store.RestoreTenantArchiveAsync(new CanonicalArchiveRestoreRequest { Archive = archive, ExpectedContentHash = archive.ContentHash });
            Assert.NotNull(await store.GetDocumentAsync("tenant-a", "entity", "e-1"));
            Assert.Null(await store.GetDocumentAsync("tenant-a", "entity", "e-2"));

            archive.Chunks[0].Content[0] ^= 0x01;
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreTenantArchiveAsync(new CanonicalArchiveRestoreRequest { Archive = archive, ExpectedContentHash = archive.ContentHash }));
        });
    }

    protected async Task RunCanonicalStore_DataPlanePreflightRestoresIsolatesAndCleansUp()
    {
        await WithStoreAsync(async store =>
        {
            var result = await store.RunDataPlanePreflightAsync();

            Assert.Equal(CanonicalDataPlanePreflightResult.ProfileV1, result.Profile);
            Assert.True(result.Ready);
            Assert.Equal(CanonicalPreflightCheckStatuses.Passed, result.Status);
            Assert.True(result.BackupRestoreVerified);
            Assert.True(result.TenantIsolationVerified);
            Assert.True(result.CleanupVerified);
            Assert.True(result.ArchiveChunkCount > 1);
            Assert.Equal(3, result.Checks.Count);
            Assert.All(result.Checks, check => Assert.Equal(CanonicalPreflightCheckStatuses.Passed, check.Status));
        });
    }

    protected async Task RunCanonicalStore_CanonicalizesEquivalentIdempotentRequests()
    {
        await WithStoreAsync(async store =>
        {
            var first = CreateUpsert("tenant-a", "canonical-json", "entity", "e-1", "Ada", "ada@example.test");
            first.IdempotencyKey = "canonical-json ";
            var firstDocument = first.Mutations[0].Document!;
            firstDocument.Data = new JsonObject { ["name"] = "Ada", ["profile"] = new JsonObject { ["last"] = "Lovelace", ["first"] = "Ada" } };
            firstDocument.Indexes = new Dictionary<string, string> { ["name"] = "Ada", ["email"] = "ada@example.test" };
            await store.CommitAsync(first);

            var replay = CreateUpsert("tenant-a", "canonical-json", "entity", "e-1", "Ada", "ada@example.test");
            var replayDocument = replay.Mutations[0].Document!;
            replayDocument.Data = new JsonObject { ["profile"] = new JsonObject { ["first"] = "Ada", ["last"] = "Lovelace" }, ["name"] = "Ada" };
            replayDocument.Indexes = new Dictionary<string, string> { ["email"] = "ada@example.test", ["name"] = "Ada" };
            Assert.True((await store.CommitAsync(replay)).Replayed);
        });
    }

    protected async Task RunCanonicalStore_QueriesProjectedRangeAndStableOrder()
    {
        await WithStoreAsync(async store =>
        {
            foreach (var (id, rank) in new[] { ("e-1", "020"), ("e-2", "010"), ("e-3", "030") })
            {
                var request = CreateUpsert("tenant-a", "rank-" + id, "review", id, id, id + "@example.test");
                request.Mutations[0].Document!.Indexes["rank"] = rank;
                await store.CommitAsync(request);
            }

            var first = await store.QueryDocumentsAsync(new CanonicalDocumentQuery
            {
                TenantId = "tenant-a", DocumentType = "review", OrderByIndex = "rank", OrderDirection = CanonicalDocumentOrderDirections.Descending,
                IndexRange = new CanonicalDocumentIndexRange { Name = "rank", GreaterThanOrEqual = "010", LessThanOrEqual = "030" }, Limit = 1
            });
            Assert.Equal("e-3", Assert.Single(first.Items).Id);
            Assert.NotNull(first.ContinuationToken);
            var second = await store.QueryDocumentsAsync(new CanonicalDocumentQuery
            {
                TenantId = "tenant-a", DocumentType = "review", OrderByIndex = "rank", OrderDirection = CanonicalDocumentOrderDirections.Descending,
                IndexRange = new CanonicalDocumentIndexRange { Name = "rank", GreaterThanOrEqual = "010", LessThanOrEqual = "030" }, Limit = 1,
                ContinuationToken = first.ContinuationToken
            });
            Assert.Equal("e-1", Assert.Single(second.Items).Id);
            Assert.NotNull(second.ContinuationToken);
            var third = await store.QueryDocumentsAsync(new CanonicalDocumentQuery
            {
                TenantId = "tenant-a", DocumentType = "review", OrderByIndex = "rank", OrderDirection = CanonicalDocumentOrderDirections.Descending,
                IndexRange = new CanonicalDocumentIndexRange { Name = "rank", GreaterThanOrEqual = "010", LessThanOrEqual = "030" }, Limit = 1,
                ContinuationToken = second.ContinuationToken
            });
            Assert.Equal("e-2", Assert.Single(third.Items).Id);
            Assert.Null(third.ContinuationToken);
        });
    }

    protected async Task RunCanonicalStore_MigrationsAndTenantSnapshotAreDurable()
    {
        await WithStoreAsync(async store =>
        {
            var migration = new CanonicalMigration { Namespace = "conformance", Id = "20260712.canon-v1", Checksum = "sha256:one", Description = "initial canonical schema" };
            await store.ApplyMigrationsAsync(new[] { migration });
            await store.ApplyMigrationsAsync(new[] { migration });
            Assert.Single(await store.ListMigrationsAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyMigrationsAsync(new[] { new CanonicalMigration { Namespace = migration.Namespace, Id = migration.Id, Checksum = "sha256:two" } }));
            await store.ApplyMigrationsAsync(new[] { new CanonicalMigration { Namespace = "another-consumer", Id = migration.Id, Checksum = "sha256:two" } });
            Assert.Equal(2, (await store.ListMigrationsAsync()).Count);

            await store.CommitAsync(CreateUpsert("tenant-a", "before-export", "entity", "e-1", "Before", "before@example.test", includeOutbox: true));
            var snapshot = await store.ExportTenantAsync("tenant-a");
            await store.CommitAsync(CreateUpsert("tenant-a", "after-export", "entity", "e-2", "After", "after@example.test"));
            await store.RestoreTenantAsync(new CanonicalRestoreRequest { Snapshot = snapshot, ExpectedContentHash = snapshot.ContentHash });

            Assert.NotNull(await store.GetDocumentAsync("tenant-a", "entity", "e-1"));
            Assert.Null(await store.GetDocumentAsync("tenant-a", "entity", "e-2"));
            Assert.True((await store.CommitAsync(CreateUpsert("tenant-a", "before-export", "entity", "e-1", "Before", "before@example.test", includeOutbox: true))).Replayed);
        });
    }

    protected async Task RunCanonicalStore_IsolatesTenants()
    {
        await WithStoreAsync(async store =>
        {
            await store.CommitAsync(CreateUpsert("tenant-a", "shared-id", "entity", "same", "A", "a@example.test"));
            await store.CommitAsync(CreateUpsert("tenant-b", "shared-id", "entity", "same", "B", "b@example.test"));
            Assert.Equal("A", (await store.GetDocumentAsync("tenant-a", "entity", "same"))!.Data!["name"]!.GetValue<string>());
            Assert.Equal("B", (await store.GetDocumentAsync("tenant-b", "entity", "same"))!.Data!["name"]!.GetValue<string>());
        });
    }

    private static CanonicalTransactionRequest CreateUpsert(string tenantId, string idempotencyKey, string documentType, string id, string name, string email, bool includeOutbox = false) =>
        new()
        {
            TenantId = tenantId,
            IdempotencyKey = idempotencyKey,
            Mutations = new List<CanonicalMutation>
            {
                new()
                {
                    Operation = CanonicalMutationOperations.Upsert,
                    Document = new CanonicalDocument
                    {
                        TenantId = tenantId,
                        DocumentType = documentType,
                        Id = id,
                        SchemaVersion = "v1",
                        Data = new JsonObject { ["name"] = name, ["email"] = email },
                        Indexes = new Dictionary<string, string> { ["email"] = email }
                    }
                }
            },
            Outbox = includeOutbox
                ? new List<CanonicalOutboxWrite> { new() { Topic = "canonical.changed", Key = id, Payload = new JsonObject { ["id"] = id } } }
                : new List<CanonicalOutboxWrite>()
        };

    private async Task WithStoreAsync(Func<ICanonicalStore, Task> scenario)
    {
        var store = await CreateStoreAsync();
        try
        {
            await scenario(store);
        }
        finally
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}
