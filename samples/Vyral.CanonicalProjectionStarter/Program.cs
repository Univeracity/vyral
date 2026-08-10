using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;
using Vyral.CanonicalProjectionStarter;
using Vyral.Local;

var workRoot = Path.Combine(Path.GetTempPath(), $"vyral-canonical-projection-{Guid.NewGuid():N}");
Directory.CreateDirectory(workRoot);
var canonical = new SqliteCanonicalStore(Path.Combine(workRoot, "canonical.sqlite"));
var projection = new SqliteCanonicalProjection(new SqliteCanonicalProjectionOptions
{
    DatabasePath = Path.Combine(workRoot, "projection.sqlite")
});
const string tenantId = "sample-tenant";

await canonical.CommitAsync(CreateCustomer("customer-1", "Ada", 1, "sample:create"));
var rebuilt = await projection.RebuildAsync(canonical, tenantId);
await canonical.CommitAsync(CreateCustomer("customer-1", "Ada Lovelace", 2, "sample:update"));
var pumped = await projection.PumpOnceAsync(canonical, tenantId);
var customer = await projection.GetAsync(tenantId, "customer", "customer-1");

Console.WriteLine($"rebuildHash={rebuilt.SnapshotContentHash} documents={rebuilt.DocumentCount}");
Console.WriteLine($"leased={pumped.Leased} applied={pumped.Applied} duplicate={pumped.Duplicate}");
Console.WriteLine($"projectedRevision={customer?.Revision} name={customer?.Data?["name"]}");
Console.WriteLine($"workRoot={workRoot}");

static CanonicalTransactionRequest CreateCustomer(string id, string name, long revision, string idempotencyKey) =>
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
                    DocumentType = "customer",
                    Id = id,
                    SchemaVersion = "v1",
                    Data = new JsonObject { ["name"] = name },
                    Indexes = new Dictionary<string, string> { ["name"] = name }
                },
                Precondition = revision > 1
                    ? new CanonicalWritePrecondition { ExpectedRevision = revision - 1, MustExist = true }
                    : null
            }
        ],
        Outbox =
        [
            new CanonicalOutboxWrite
            {
                Topic = "canonical.document.changed",
                Key = id,
                Payload = new JsonObject
                {
                    ["documentType"] = "customer",
                    ["documentId"] = id,
                    ["revision"] = revision
                }
            }
        ]
    };
