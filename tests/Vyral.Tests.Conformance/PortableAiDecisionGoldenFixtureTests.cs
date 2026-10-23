using System.Text.Json;
using Vyral.Providers.Abstractions;

namespace Vyral.Tests.Conformance;

public sealed class PortableAiDecisionGoldenFixtureTests
{
    [Fact]
    public void DotNetEvaluationMatchesThePortableDecisionFixture()
    {
        using var manifest = JsonDocument.Parse(ReadResource("Vyral.Tests.Conformance.ai-decision-v1-manifest.json"));
        var policy = JsonSerializer.Deserialize<AiDecisionPolicy>(
            ReadResource("Vyral.Tests.Conformance.ai-decision-v1-policy.json"), ProviderJson.Options)!;
        var result = JsonSerializer.Deserialize<ProviderRunResult>(
            ReadResource("Vyral.Tests.Conformance.ai-decision-v1-provider-result.json"), ProviderJson.Options)!;
        var expected = JsonSerializer.Deserialize<AiDecisionReceipt>(
            ReadResource("Vyral.Tests.Conformance.ai-decision-v1-receipt.json"), ProviderJson.Options)!;

        var actual = AiDecisionPolicyEvaluator.Evaluate(
            policy,
            result,
            DateTimeOffset.Parse("2026-10-23T05:30:00Z"));

        Assert.Equal(manifest.RootElement.GetProperty("expectedPolicyHash").GetString(), actual.PolicyHash);
        Assert.Equal(manifest.RootElement.GetProperty("expectedAnswerHash").GetString(), actual.Evidence.AnswerHash);
        Assert.Equal(manifest.RootElement.GetProperty("expectedOutcome").GetString(), actual.Outcome);
        Assert.Equal(manifest.RootElement.GetProperty("expectedReason").GetString(), actual.Reason);
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.PolicyId, actual.PolicyId);
        Assert.Equal(expected.PolicyVersion, actual.PolicyVersion);
        Assert.Equal(expected.PolicyHash, actual.PolicyHash);
        Assert.Equal(expected.QuestionId, actual.QuestionId);
        Assert.Equal(expected.AnswerKind, actual.AnswerKind);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.EscalationRoute, actual.EscalationRoute);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.ProviderRunStatus, actual.ProviderRunStatus);
        Assert.Equal(expected.ProviderStatus, actual.ProviderStatus);
        Assert.Equal(expected.ModelId, actual.ModelId);
        Assert.Equal(expected.ValidationStatus, actual.ValidationStatus);
        Assert.Equal(expected.Choice, actual.Choice);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Confidence, actual.Confidence);
        Assert.Equal(expected.Calibrated, actual.Calibrated);
        Assert.Equal(expected.LabelMassCoverage, actual.LabelMassCoverage);
        Assert.Equal(expected.RotationAgreement, actual.RotationAgreement);
        Assert.Equal(expected.EvaluatedAtUtc, actual.EvaluatedAtUtc);
        Assert.Equal(expected.Evidence.TraceId, actual.Evidence.TraceId);
        Assert.Equal(expected.Evidence.AdapterId, actual.Evidence.AdapterId);
        Assert.Equal(expected.Evidence.ConfigHash, actual.Evidence.ConfigHash);
        Assert.Equal(expected.Evidence.InputHash, actual.Evidence.InputHash);
        Assert.Equal(expected.Evidence.OutputHash, actual.Evidence.OutputHash);
        Assert.Equal(expected.Evidence.AnswerHash, actual.Evidence.AnswerHash);
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableAiDecisionGoldenFixtureTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
