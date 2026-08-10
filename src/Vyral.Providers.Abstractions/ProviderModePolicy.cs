using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

public static class ProviderModes
{
    /// <summary>Lightweight qualification and smoke-test runs. Minimal input/output budgets, shortest timeout.</summary>
    public const string Mechanics = "mechanics";
    /// <summary>Development-time runs with relaxed budgets and no review gate.</summary>
    public const string Development = "development";
    /// <summary>Standard advisory mode: proposals and evidence only, human review required.</summary>
    public const string Advisory = "advisory";
    /// <summary>Read-only public-web research for a response; source writes remain forbidden.</summary>
    public const string Research = "research";
    /// <summary>Review and finding mode: allows finding and question outputs alongside evidence.</summary>
    public const string Review = "review";
    /// <summary>Scaffold mode: allows proposal, patch, and artifact outputs with larger budgets.</summary>
    public const string Scaffold = "scaffold";
    /// <summary>Autonomous mode: direct actions permitted; requires explicit policy enablement.</summary>
    public const string Autonomous = "autonomous";
}

public static class ProviderToolPolicies
{
    /// <summary>Tools are defined and managed by the caller; the provider executes them as requested.</summary>
    public const string CallerOwned = "caller-owned";
    /// <summary>The provider manages its own tool set.</summary>
    public const string ProviderOwned = "provider-owned";
    /// <summary>
    /// The execution host, rather than a model prompt, enforces the process and
    /// workspace boundary and directly executes declared validation commands.
    /// </summary>
    public const string HostEnforced = "host-enforced";
}

public static class ProviderOutputKinds
{
    /// <summary>Output is a proposal requiring human review before action.</summary>
    public const string Proposal = "proposal";
    /// <summary>Output is evidence or analysis used to inform a decision.</summary>
    public const string Evidence = "evidence";
    /// <summary>Output is a direct action or write; requires autonomous mode.</summary>
    public const string Action = "action";
    /// <summary>Output is a review finding (issue, observation, or annotation).</summary>
    public const string Finding = "finding";
    /// <summary>Output is a clarifying question to the caller or reviewer.</summary>
    public const string Question = "question";
    /// <summary>Output is a code or content patch to be applied.</summary>
    public const string Patch = "patch";
    /// <summary>Output is a generated artifact (file, document, or resource).</summary>
    public const string Artifact = "artifact";
}

public sealed class ProviderModePolicy
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = ProviderModes.Advisory;

    [JsonPropertyName("allowedOutputKinds")]
    public List<string> AllowedOutputKinds { get; set; } = new() { ProviderOutputKinds.Proposal, ProviderOutputKinds.Evidence };

    [JsonPropertyName("maxInputBytes")]
    public int MaxInputBytes { get; set; } = 64 * 1024;

    [JsonPropertyName("maxOutputBytes")]
    public int MaxOutputBytes { get; set; } = 128 * 1024;

    [JsonPropertyName("toolPolicy")]
    public string ToolPolicy { get; set; } = ProviderToolPolicies.CallerOwned;

    [JsonPropertyName("allowNetwork")]
    public bool AllowNetwork { get; set; } = true;

    [JsonPropertyName("allowSourceWrites")]
    public bool AllowSourceWrites { get; set; }

    [JsonPropertyName("reviewRequired")]
    public bool ReviewRequired { get; set; } = true;

    [JsonPropertyName("traceRequired")]
    public bool TraceRequired { get; set; } = true;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 120;
}
