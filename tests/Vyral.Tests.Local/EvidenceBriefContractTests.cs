using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public sealed class EvidenceBriefContractTests
{
    [Fact]
    public void ValidFixture_RoundTripsThroughCanonicalDocument()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "contracts", "evidence-brief.v1.schema.json");
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "evidence-brief.v1.valid.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var brief = node.Deserialize<EvidenceBrief>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.Equal(EvidenceBriefContract.SchemaV1, schema["properties"]!["schema"]!["const"]!.GetValue<string>());
        EvidenceBriefContract.Validate(brief);
        var document = EvidenceBriefContract.ToCanonicalDocument("tenant-a", brief);
        var roundTripped = EvidenceBriefContract.FromCanonicalDocument(document);

        Assert.Equal(EvidenceBriefContract.SchemaV1, document.SchemaVersion);
        Assert.Equal("2026-07-21T12:00:00.0000000Z", document.Indexes["asOfUtc"]);
        Assert.Equal(brief.Question, roundTripped.Brief.Question);
        Assert.Equal("official-schedule", Assert.Single(roundTripped.Brief.SourceSnapshots).Id);
    }

    [Fact]
    public void Contract_RejectsCredentialLikeOrUnstableSourceLocator()
    {
        var brief = EvidenceBriefTestData.Create();
        brief.SourceSnapshots[0].Uri = "https://example.test/rates/schedule?temporary-token=not-allowed";

        var exception = Assert.Throws<InvalidOperationException>(() => EvidenceBriefContract.Validate(brief));

        Assert.Contains("query parameters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_RejectsMissingCitationSourceReference()
    {
        var brief = EvidenceBriefTestData.Create();
        brief.Citations[0].SourceSnapshotId = null!;

        var exception = Assert.Throws<InvalidOperationException>(() => EvidenceBriefContract.Validate(brief));

        Assert.Contains("sourceSnapshotId is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalStoreHelper_CommitsTypedBriefAndProjectionWakeupAtomically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-evidence-brief-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteCanonicalStore(path);
        var request = new EvidenceBriefWriteRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "brief:rates:2026-07-21:v1",
            Brief = EvidenceBriefTestData.Create()
        };

        var committed = await store.StoreEvidenceBriefAsync(request);
        var stored = await store.GetEvidenceBriefAsync("tenant-a", request.Brief.Id);
        var lease = Assert.Single(await store.LeaseOutboxAsync(new CanonicalOutboxLeaseRequest
        {
            TenantId = "tenant-a",
            ConsumerId = "evidence-projection",
            LeaseSeconds = 60
        }));

        Assert.False(committed.Replayed);
        Assert.Equal(request.Brief.Id, Assert.Single(committed.Documents).Id);
        Assert.NotNull(stored);
        Assert.Equal(request.Brief.Question, stored!.Brief.Question);
        Assert.Equal(EvidenceBriefContract.DefaultChangedEventTopic, lease.Event.Topic);
        Assert.Equal(request.Brief.Id, lease.Event.Payload!["briefId"]!.GetValue<string>());
    }
}

internal static class EvidenceBriefTestData
{
    public static EvidenceBrief Create() => new()
    {
        Id = "brief-rates-2026-07-21",
        Question = "What rate was published as of 2026-07-21?",
        AsOfUtc = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc),
        FactAnchors =
        [
            new EvidenceBriefFactAnchor
            {
                Id = "rate-published",
                Statement = "The official schedule lists the rate as 4.25 percent.",
                SourceSnapshotIds = ["official-schedule"],
                CitationIds = ["official-schedule-page-4"]
            }
        ],
        SourceSnapshots =
        [
            new EvidenceBriefSourceSnapshot
            {
                Id = "official-schedule",
                Kind = "web",
                Uri = "https://example.test/rates/schedule",
                ContentHash = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                CapturedAtUtc = new DateTime(2026, 7, 21, 11, 59, 0, DateTimeKind.Utc),
                PublishedAtUtc = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc)
            }
        ],
        Citations =
        [
            new EvidenceBriefCitation
            {
                Id = "official-schedule-page-4",
                SourceSnapshotId = "official-schedule",
                FactAnchorIds = ["rate-published"],
                DisplayText = "Official rate schedule, page 4",
                Locator = "p. 4"
            }
        ],
        Uncertainties =
        [
            new EvidenceBriefUncertainty
            {
                Id = "publication-scope",
                Statement = "The schedule states a published rate and does not establish an individual applicant's final rate.",
                Level = EvidenceBriefUncertaintyLevels.Medium,
                FactAnchorIds = ["rate-published"]
            }
        ],
        RetrievalTraces =
        [
            new EvidenceBriefRetrievalTrace
            {
                TraceId = "trace-rates-2026-07-21",
                RetrievedAtUtc = new DateTime(2026, 7, 21, 11, 58, 0, DateTimeKind.Utc),
                QueryHash = "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
                Matches =
                [
                    new EvidenceBriefRetrievalMatch
                    {
                        Collection = "rates-public",
                        RecordId = "schedule-2026-07-page-4",
                        Rank = 1,
                        SourceSnapshotIds = ["official-schedule"]
                    }
                ]
            }
        ]
    };
}
