using System.Text.Json;
using Vyral.Providers.Abstractions;

namespace Vyral.Tests.Conformance;

public sealed class PortableAiDecisionLedgerGoldenFixtureTests
{
    [Fact]
    public void DotNetHashesMatchThePortableLedgerEntryFixture()
    {
        using var manifest = JsonDocument.Parse(ReadResource(
            "Vyral.Tests.Conformance.ai-decision-ledger-v1-manifest.json"));
        var expected = JsonSerializer.Deserialize<AiDecisionLedgerEntry>(ReadResource(
            "Vyral.Tests.Conformance.ai-decision-ledger-v1-entry.json"), ProviderJson.Options)!;

        var actual = AiDecisionLedger.CreateEntry(
            expected.Receipt,
            0,
            null,
            DateTimeOffset.Parse("2026-10-23T06:30:00Z"));

        Assert.Empty(AiDecisionLedger.ValidateEntry(expected));
        Assert.Equal(manifest.RootElement.GetProperty("receiptHash").GetString(),
            AiDecisionLedger.ComputeReceiptHash(expected.Receipt));
        Assert.Equal(manifest.RootElement.GetProperty("receiptHash").GetString(), actual.ReceiptHash);
        Assert.Equal(manifest.RootElement.GetProperty("entryHash").GetString(), actual.EntryHash);
        Assert.Equal(expected.RecordedAtUtc, actual.RecordedAtUtc);
        Assert.Equal(expected.EntryHash, actual.EntryHash);
    }

    [Fact]
    public void ChainValidationFailsClosedOnMutationAndBrokenLinks()
    {
        var first = JsonSerializer.Deserialize<AiDecisionLedgerEntry>(ReadResource(
            "Vyral.Tests.Conformance.ai-decision-ledger-v1-entry.json"), ProviderJson.Options)!;
        var second = AiDecisionLedger.CreateEntry(
            first.Receipt,
            1,
            first.EntryHash,
            DateTimeOffset.Parse("2026-10-23T06:31:00Z"));

        Assert.Empty(AiDecisionLedger.VerifyChain(new[] { first, second }));

        second.Receipt.PolicyVersion = "tampered";
        var errors = AiDecisionLedger.VerifyChain(new[] { first, second });
        Assert.Contains(errors, error => error.Contains(
            "receiptHash does not match receipt", StringComparison.Ordinal));

        second.Receipt.PolicyVersion = first.Receipt.PolicyVersion;
        second.PreviousEntryHash = "sha256:" + new string('0', 64);
        errors = AiDecisionLedger.VerifyChain(new[] { first, second });
        Assert.Contains("entry[1].previousEntryHash does not match", errors);
    }

    [Fact]
    public void ReceiptValidationRejectsUnknownPropertiesAndInconsistentBranches()
    {
        var receipt = JsonSerializer.Deserialize<AiDecisionReceipt>(ReadResource(
            "Vyral.Tests.Conformance.ai-decision-v1-receipt.json"), ProviderJson.Options)!;
        receipt.AdditionalProperties = new Dictionary<string, JsonElement>
        {
            ["unknown"] = JsonSerializer.SerializeToElement(true)
        };
        receipt.EscalationRoute = "manual-review";

        var errors = AiDecisionLedger.ValidateReceipt(receipt);

        Assert.Contains("receipt contains unknown properties", errors);
        Assert.Contains("accept outcome cannot include escalationRoute", errors);
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableAiDecisionLedgerGoldenFixtureTests).Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
