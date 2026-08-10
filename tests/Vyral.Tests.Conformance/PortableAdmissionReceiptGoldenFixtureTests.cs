using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Execution;

namespace Vyral.Tests.Conformance;

public sealed class PortableAdmissionReceiptGoldenFixtureTests
{
    private const string ScenarioId = "admission.receipts.v1";
    private const string ManifestResource =
        "Vyral.Tests.Conformance.runtime-v1-manifest.json";
    private const string ScenarioResource =
        "Vyral.Tests.Conformance.runtime-v1-admission-receipts.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void AdmissionReceiptsMatchPortableGoldens()
    {
        var manifestBytes = ReadResource(ManifestResource);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var descriptor = manifest.RootElement
            .GetProperty("scenarios")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("id").GetString() == ScenarioId);
        Assert.Equal(
            "vyral.runtime.contracts.v1",
            descriptor.GetProperty("profile").GetString());
        Assert.Equal("golden", descriptor.GetProperty("kind").GetString());

        var scenarioBytes = ReadResource(ScenarioResource);
        var digest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(scenarioBytes));
        Assert.Equal(
            descriptor.GetProperty("sha256").GetString(),
            digest);

        using var scenario = JsonDocument.Parse(scenarioBytes);
        foreach (var step in scenario.RootElement
            .GetProperty("steps")
            .EnumerateArray())
        {
            Assert.Equal(
                "admission.receipt",
                step.GetProperty("operation").GetString());
            var arguments = step.GetProperty("arguments");
            var rejected = string.Equals(
                arguments.GetProperty("status").GetString(),
                "rejected",
                StringComparison.Ordinal);
            var run = new ExecutionRun
            {
                Id = arguments.GetProperty("resourceId").GetString()!,
                Status = rejected
                    ? ExecutionRunStatuses.Rejected
                    : ExecutionRunStatuses.Queued,
                PayloadHash = arguments
                    .GetProperty("requestHash")
                    .GetString()!,
                IdempotencyKey = OptionalString(
                    arguments,
                    "idempotencyKey"),
                AdmissionReplayed = arguments
                    .GetProperty("replayed")
                    .GetBoolean(),
                CreatedAtUtc = arguments
                    .GetProperty("admittedAtUtc")
                    .GetDateTime(),
                FailureClass = OptionalString(
                    arguments,
                    "failureClass"),
                Error = OptionalString(arguments, "error")
            };
            var receipt = ExecutionAdmission.Create(
                run,
                arguments.GetProperty("operationId").GetString()!,
                arguments.GetProperty("statusUri").GetString()!,
                OptionalString(arguments, "resultUri"));
            var actual = JsonSerializer.SerializeToNode(
                receipt,
                JsonOptions);
            var expected = JsonNode.Parse(
                step.GetProperty("expect")
                    .GetProperty("value")
                    .GetRawText());

            Assert.True(
                JsonNode.DeepEquals(expected, actual),
                $"Admission step " +
                $"'{step.GetProperty("id").GetString()}' produced " +
                $"{actual?.ToJsonString()}, expected " +
                $"{expected?.ToJsonString()}.");
        }
    }

    private static string? OptionalString(
        JsonElement value,
        string propertyName) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(
            PortableAdmissionReceiptGoldenFixtureTests)
            .Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded conformance resource '{name}' " +
                "is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
