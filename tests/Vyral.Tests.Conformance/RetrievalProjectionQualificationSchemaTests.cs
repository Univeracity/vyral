using System.Text.Json.Nodes;

namespace Vyral.Tests.Conformance;

public sealed class RetrievalProjectionQualificationSchemaTests
{
    [Fact]
    public void RetrievalQualificationIsSeparateFromGenerationAndExecutionEvidence()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "qualification",
            "retrieval-projection-qualification.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            "Vyral retrieval projection adapter qualification artifact",
            schema["title"]!.GetValue<string>());
        var policy = schema["$defs"]!["generationEvidencePolicy"]!["properties"]!;
        Assert.True(policy["requiresExactGenerationBinding"]!["const"]!.GetValue<bool>());
        Assert.True(policy["requiresCompleteRequestedCoverage"]!["const"]!.GetValue<bool>());
        Assert.False(policy["mutableHealthEstablishesCompleteness"]!["const"]!.GetValue<bool>());
        Assert.Equal(
            "vyral.record-search-projection-generation.v1",
            policy["descriptorSchema"]!["const"]!.GetValue<string>());
        var evidence = schema["$defs"]!["evidence"]!;
        var evidenceKinds = evidence["properties"]!["kind"]!["enum"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .ToList();
        Assert.Contains("local_gate", evidenceKinds);
        var requiredEvidence = evidence["required"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .ToList();
        Assert.Contains("sourceTreeDigest", requiredEvidence);
        Assert.Contains("sourceDirty", requiredEvidence);
        var adapterRequired = schema["$defs"]!["adapter"]!["required"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .ToList();
        Assert.Contains("implementationArtifacts", adapterRequired);
    }
}
