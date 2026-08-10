namespace Vyral.Execution;

public sealed class ExecutionRuntimeLimits
{
    public static ExecutionRuntimeLimits Default => new();

    public int MaxIdChars { get; init; } = 160;
    public int MaxDisplayNameChars { get; init; } = 200;
    public int MaxDescriptionChars { get; init; } = 1_000;
    public int MaxPayloadBytes { get; init; } = 1_048_576;
    public int MaxResultBytes { get; init; } = 1_048_576;
    public int MaxStatusDetailsBytes { get; init; } = 65_536;
    public int MaxTraceMessageChars { get; init; } = 4_096;
    public int MaxTraceDetailsBytes { get; init; } = 65_536;
    public int MaxArtifactBytes { get; init; } = 16_777_216;
    public int MaxArtifactInlineBytes { get; init; } = 1_048_576;
    public int MaxArtifactNameChars { get; init; } = 160;
    public int MaxCheckpointBytes { get; init; } = 1_048_576;
    public int MaxCheckpointKeyChars { get; init; } = 160;
    public int MaxTagCount { get; init; } = 32;
    public int MaxTagKeyChars { get; init; } = 80;
    public int MaxTagValueChars { get; init; } = 512;
    public int MaxRetryAttempts { get; init; } = 25;
    public double MaxRetryDelaySeconds { get; init; } = 86_400;
    public double MaxLeaseTtlSeconds { get; init; } = 86_400;
}
