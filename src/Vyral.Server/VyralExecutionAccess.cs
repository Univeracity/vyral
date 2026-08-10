using Google.Apis.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vyral.Execution;
using Vyral.Mcp;

namespace Vyral.Server;

/// <summary>
/// Product and worker authorization for the execution HTTP surface. API keys can still protect
/// generic server routes, but a shared execution plane requires a verified workload identity that
/// is mapped to an explicit product/tenant/worker policy.
/// </summary>
public sealed class VyralExecutionAccess : IVyralMcpExecutionAuthorizer
{
    private readonly IHostEnvironment _environment;
    private readonly IReadOnlyDictionary<string, IExecutionIdentityAuthenticator> _authenticators;
    private readonly Dictionary<string, IReadOnlyList<VyralExecutionIdentityPolicy>> _policies;

    public VyralExecutionAccess(VyralExecutionAccessOptions options, IHostEnvironment environment)
        : this(options, environment, CreateDefaultAuthenticators())
    {
    }

    public VyralExecutionAccess(
        VyralExecutionAccessOptions options,
        IHostEnvironment environment,
        IEnumerable<IExecutionIdentityAuthenticator> authenticators)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ArgumentNullException.ThrowIfNull(authenticators);
        _authenticators = authenticators
            .Where(authenticator => !string.IsNullOrWhiteSpace(authenticator.AuthenticationMode))
            .ToDictionary(authenticator => authenticator.AuthenticationMode, StringComparer.Ordinal);
        if (Enabled && !_authenticators.ContainsKey(Options.AuthenticationMode))
        {
            throw new InvalidOperationException($"Execution identity authentication mode '{Options.AuthenticationMode}' is not registered by this host.");
        }
        _policies = Options.IdentityPolicies.GroupBy(policy => policy.Principal, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<VyralExecutionIdentityPolicy>)group.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public VyralExecutionAccessOptions Options { get; }
    public bool Enabled => Options.IdentityPolicies.Count > 0;

    public async Task BindStartRunAsync(HttpContext context, ExecutionRunRequest request, CancellationToken ct = default)
    {
        if (!Enabled) return;
        ArgumentNullException.ThrowIfNull(request);
        var caller = await ResolveAsync(context, ct);
        var scope = request.Scope ?? throw new ExecutionAccessDeniedException("Execution scope is required for a shared execution plane.");
        _ = FindPolicy(caller.Principal, scope.ProductId, scope.TenantId, request.HandlerId, ExecutionAccessOperations.StartRun);
        scope.ServiceIdentity = caller.Principal;
        request.Scope = scope;
    }

    public async Task BindWorkerAsync(HttpContext context, ExecutionExternalWorkerLeaseRequest request, CancellationToken ct = default)
    {
        if (!Enabled) return;
        ArgumentNullException.ThrowIfNull(request);
        var caller = await ResolveAsync(context, ct);
        var candidates = GetPolicies(caller.Principal).Where(policy => policy.AllowedOperations.Contains(ExecutionAccessOperations.Worker))
            .Where(policy => !string.IsNullOrWhiteSpace(policy.WorkerId)).ToList();
        var policy = candidates.FirstOrDefault(policy => string.Equals(policy.WorkerId, request.WorkerId, StringComparison.Ordinal));
        if (policy is null) throw new ExecutionAccessDeniedException("The verified workload identity is not allowed to use this execution worker id.");
        foreach (var handlerId in request.HandlerIds)
        {
            if (!candidates.Any(candidate => string.Equals(candidate.WorkerId, policy.WorkerId, StringComparison.Ordinal) && IsHandlerAllowed(candidate, handlerId)))
                throw new ExecutionAccessDeniedException($"Execution worker '{policy.WorkerId}' is not allowed to claim handler '{handlerId}'.");
        }

        request.WorkerId = policy.WorkerId!;
    }

    public async Task BindWorkerAsync(HttpContext context, string workerId, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var caller = await ResolveAsync(context, ct);
        if (!GetPolicies(caller.Principal).Any(policy => policy.AllowedOperations.Contains(ExecutionAccessOperations.Worker) && string.Equals(policy.WorkerId, workerId, StringComparison.Ordinal)))
            throw new ExecutionAccessDeniedException("The verified workload identity is not allowed to use this execution worker id.");
    }

    public async Task AuthorizeRunAsync(HttpContext context, ExecutionRun? run, string operation, CancellationToken ct = default)
    {
        if (!Enabled || run is null) return;
        var caller = await ResolveAsync(context, ct);
        var scope = run.Scope ?? throw new ExecutionAccessDeniedException("Execution run does not contain a product scope.");
        _ = FindPolicy(caller.Principal, scope.ProductId, scope.TenantId, run.HandlerId, operation);
    }

    public async Task AuthorizeMaintenanceAsync(HttpContext context, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var caller = await ResolveAsync(context, ct);
        if (!GetPolicies(caller.Principal).Any(policy => policy.AllowedOperations.Contains(ExecutionAccessOperations.Maintenance)))
            throw new ExecutionAccessDeniedException("The verified workload identity is not allowed to maintain execution state.");
    }

    public bool CanReadRunLocally(string principal, ExecutionRun run)
    {
        if (!Enabled) return true;
        var scope = run.Scope;
        return scope is not null && GetPolicies(principal).Any(policy => policy.AllowedOperations.Contains(ExecutionAccessOperations.ReadRun)
            && string.Equals(policy.ProductId, scope.ProductId, StringComparison.Ordinal) && IsTenantAllowed(policy, scope.TenantId) && IsHandlerAllowed(policy, run.HandlerId));
    }

    public async Task<IReadOnlyList<ExecutionRun>> FilterReadableRunsAsync(HttpContext context, IEnumerable<ExecutionRun> runs, CancellationToken ct = default)
    {
        var materialized = runs.ToList();
        if (!Enabled) return materialized;
        var caller = await ResolveAsync(context, ct);
        return materialized.Where(run => CanReadRunLocally(caller.Principal, run)).ToList();
    }

    /// <summary>
    /// Resolves only the caller's effective start-run surface for runtime discovery. It never
    /// returns policy documents, other principals, or unrelated products/tenants.
    /// </summary>
    public async Task<VyralEffectiveExecutionRuntimeAccess> GetEffectiveRuntimeAccessAsync(
        HttpContext context,
        string? productId,
        string? tenantId,
        CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return new VyralEffectiveExecutionRuntimeAccess
            {
                SharedExecution = false,
                ScopeRequired = false,
                AllowsAnyHandler = true
            };
        }

        var caller = await ResolveAsync(context, ct);
        var startPolicies = GetPolicies(caller.Principal)
            .Where(policy => policy.AllowedOperations.Contains(ExecutionAccessOperations.StartRun))
            .ToList();
        if (startPolicies.Count == 0)
            throw new ExecutionAccessDeniedException("The verified workload identity is not allowed to start execution runs.");

        var normalizedProductId = productId?.Trim();
        var normalizedTenantId = tenantId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedProductId) || string.IsNullOrWhiteSpace(normalizedTenantId))
        {
            return new VyralEffectiveExecutionRuntimeAccess
            {
                SharedExecution = true,
                ScopeRequired = true
            };
        }

        var applicable = startPolicies
            .Where(policy => string.Equals(policy.ProductId, normalizedProductId, StringComparison.Ordinal) && IsTenantAllowed(policy, normalizedTenantId))
            .ToList();
        if (applicable.Count == 0)
            throw new ExecutionAccessDeniedException($"The verified workload identity is not allowed to discover execution handlers for product '{normalizedProductId}' and tenant '{normalizedTenantId}'.");

        return new VyralEffectiveExecutionRuntimeAccess
        {
            SharedExecution = true,
            ScopeRequired = true,
            ProductId = normalizedProductId,
            TenantId = normalizedTenantId,
            AllowsAnyHandler = applicable.Any(policy => policy.AllowedHandlerIds.Count == 0),
            AllowedHandlerIds = applicable.SelectMany(policy => policy.AllowedHandlerIds).ToHashSet(StringComparer.Ordinal)
        };
    }

    private async Task<VyralExecutionCaller> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        const string cacheKey = "vyral.execution.caller";
        if (context.Items.TryGetValue(cacheKey, out var cached) && cached is VyralExecutionCaller caller) return caller;

        if (!_authenticators.TryGetValue(Options.AuthenticationMode, out var authenticator))
        {
            throw new InvalidOperationException($"Execution identity authentication mode '{Options.AuthenticationMode}' is not registered by this host.");
        }

        var principal = await authenticator.AuthenticateAsync(context, Options, _environment, ct);

        if (!_policies.ContainsKey(principal)) throw new ExecutionAccessDeniedException("The verified workload identity is not configured for the execution surface.");
        caller = new VyralExecutionCaller { Principal = principal };
        context.Items[cacheKey] = caller;
        return caller;
    }

    private VyralExecutionIdentityPolicy FindPolicy(string principal, string productId, string tenantId, string handlerId, string operation)
    {
        var policy = GetPolicies(principal).FirstOrDefault(candidate => candidate.AllowedOperations.Contains(operation)
            && string.Equals(candidate.ProductId, productId, StringComparison.Ordinal) && IsTenantAllowed(candidate, tenantId) && IsHandlerAllowed(candidate, handlerId));
        return policy ?? throw new ExecutionAccessDeniedException($"The verified workload identity is not allowed to {operation} for product '{productId}', tenant '{tenantId}', and handler '{handlerId}'.");
    }

    private IReadOnlyList<VyralExecutionIdentityPolicy> GetPolicies(string principal) => _policies.TryGetValue(principal, out var policies) ? policies : Array.Empty<VyralExecutionIdentityPolicy>();
    private static bool IsTenantAllowed(VyralExecutionIdentityPolicy policy, string tenantId) => policy.AllowedTenantIds.Count == 0 || policy.AllowedTenantIds.Contains(tenantId);
    private static bool IsHandlerAllowed(VyralExecutionIdentityPolicy policy, string handlerId) => policy.AllowedHandlerIds.Count == 0 || policy.AllowedHandlerIds.Contains(handlerId);

    private static IReadOnlyList<IExecutionIdentityAuthenticator> CreateDefaultAuthenticators() =>
    [
        new DevelopmentHeaderExecutionIdentityAuthenticator(),
        new GoogleOidcExecutionIdentityAuthenticator()
    ];

    internal static string? GetBearerToken(HttpRequest request)
    {
        var candidate = request.Headers["X-Serverless-Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(candidate)) candidate = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate[prefix.Length..].Trim() : null;
    }
}

public sealed class VyralExecutionAccessOptions
{
    public string AuthenticationMode { get; init; } = VyralExecutionAuthenticationModes.GoogleOidc;
    public string DevelopmentIdentityHeader { get; init; } = "X-Vyral-Development-Identity";
    public IReadOnlySet<string> AllowedAudiences { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<VyralExecutionIdentityPolicy> IdentityPolicies { get; init; } = Array.Empty<VyralExecutionIdentityPolicy>();

    public static VyralExecutionAccessOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Server:ExecutionAccess");
        var policies = section.GetSection("IdentityPolicies").GetChildren().Concat(section.GetSection("identityPolicies").GetChildren()).Select(item => new VyralExecutionIdentityPolicy
        {
            Principal = item["Principal"] ?? item["principal"] ?? string.Empty,
            ProductId = item["ProductId"] ?? item["productId"] ?? string.Empty,
            WorkerId = item["WorkerId"] ?? item["workerId"],
            AllowedTenantIds = ReadValues(item, "AllowedTenantIds", "allowedTenantIds"),
            AllowedHandlerIds = ReadValues(item, "AllowedHandlerIds", "allowedHandlerIds"),
            AllowedOperations = ReadValues(item, "AllowedOperations", "allowedOperations")
        }).ToList();
        var options = new VyralExecutionAccessOptions
        {
            AuthenticationMode = section["AuthenticationMode"] ?? section["authenticationMode"] ?? VyralExecutionAuthenticationModes.GoogleOidc,
            DevelopmentIdentityHeader = section["DevelopmentIdentityHeader"] ?? section["developmentIdentityHeader"] ?? "X-Vyral-Development-Identity",
            AllowedAudiences = ReadValues(section, "AllowedAudiences", "allowedAudiences"),
            IdentityPolicies = policies
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (IdentityPolicies.Count == 0) return;
        if (string.IsNullOrWhiteSpace(AuthenticationMode)) throw new InvalidOperationException("Server:ExecutionAccess:AuthenticationMode is required when execution identity policies are configured.");
        foreach (var policy in IdentityPolicies)
        {
            if (string.IsNullOrWhiteSpace(policy.Principal) || string.IsNullOrWhiteSpace(policy.ProductId)) throw new InvalidOperationException("Execution identity policies require Principal and ProductId.");
            if (policy.AllowedOperations.Count == 0) throw new InvalidOperationException($"Execution identity policy '{policy.Principal}' must declare AllowedOperations.");
            if (policy.AllowedOperations.Contains(ExecutionAccessOperations.Worker) && string.IsNullOrWhiteSpace(policy.WorkerId)) throw new InvalidOperationException($"Execution identity policy '{policy.Principal}' must declare WorkerId when it allows worker operations.");
        }
    }

    private static IReadOnlySet<string> ReadValues(IConfigurationSection section, string pascalName, string camelName) => new HashSet<string>(section.GetSection(pascalName).GetChildren()
        .Concat(section.GetSection(camelName).GetChildren()).Select(item => item.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()), StringComparer.Ordinal);
}

public sealed class VyralExecutionIdentityPolicy
{
    public required string Principal { get; init; }
    public required string ProductId { get; init; }
    public string? WorkerId { get; init; }
    public IReadOnlySet<string> AllowedTenantIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> AllowedHandlerIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> AllowedOperations { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed class VyralEffectiveExecutionRuntimeAccess
{
    public bool SharedExecution { get; init; }
    public bool ScopeRequired { get; init; }
    public string? ProductId { get; init; }
    public string? TenantId { get; init; }
    public bool AllowsAnyHandler { get; init; }
    public IReadOnlySet<string> AllowedHandlerIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public static class VyralExecutionAuthenticationModes { public const string GoogleOidc = "google-oidc"; public const string DevelopmentHeader = "development-header"; }
public static class ExecutionAccessOperations { public const string StartRun = "start_run"; public const string ReadRun = "read_run"; public const string CancelRun = "cancel_run"; public const string RaiseEvent = "raise_event"; public const string Worker = "worker"; public const string Maintenance = "maintenance"; }
public sealed class VyralExecutionCaller { public required string Principal { get; init; } }
public sealed class ExecutionAccessDeniedException : Exception { public ExecutionAccessDeniedException(string message) : base(message) { } }

/// <summary>
/// Verifies a workload request and returns its stable service identity. The shared execution
/// policy deliberately knows only this identity; Google OIDC, Azure Entra, AWS IAM, mTLS, and
/// future host mechanisms belong in separate authenticators.
/// </summary>
public interface IExecutionIdentityAuthenticator
{
    string AuthenticationMode { get; }
    Task<string> AuthenticateAsync(HttpContext context, VyralExecutionAccessOptions options, IHostEnvironment environment, CancellationToken ct = default);
}

public sealed class DevelopmentHeaderExecutionIdentityAuthenticator : IExecutionIdentityAuthenticator
{
    public string AuthenticationMode => VyralExecutionAuthenticationModes.DevelopmentHeader;

    public Task<string> AuthenticateAsync(HttpContext context, VyralExecutionAccessOptions options, IHostEnvironment environment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!environment.IsDevelopment()) throw new ExecutionAccessDeniedException("Development-header execution identity authentication is disabled outside Development.");
        var principal = context.Request.Headers[options.DevelopmentIdentityHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(principal)) throw new ExecutionAccessDeniedException("A development execution identity header is required.");
        return Task.FromResult(principal);
    }
}

public interface IGoogleExecutionTokenValidator { Task<string> ValidateAsync(string token, IReadOnlySet<string> allowedAudiences, CancellationToken ct = default); }

/// <summary>Google Cloud Run/Google-issued OIDC implementation of the host identity seam.</summary>
public sealed class GoogleOidcExecutionIdentityAuthenticator : IExecutionIdentityAuthenticator
{
    private readonly IGoogleExecutionTokenValidator _tokenValidator;

    public GoogleOidcExecutionIdentityAuthenticator(IGoogleExecutionTokenValidator? tokenValidator = null)
    {
        _tokenValidator = tokenValidator ?? new GoogleExecutionTokenValidator();
    }

    public string AuthenticationMode => VyralExecutionAuthenticationModes.GoogleOidc;

    public async Task<string> AuthenticateAsync(HttpContext context, VyralExecutionAccessOptions options, IHostEnvironment environment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (options.AllowedAudiences.Count == 0) throw new InvalidOperationException("Server:ExecutionAccess:AllowedAudiences is required for Google OIDC execution identity authentication.");
        var token = VyralExecutionAccess.GetBearerToken(context.Request);
        if (string.IsNullOrWhiteSpace(token)) throw new ExecutionAccessDeniedException("A Google OIDC identity token is required for the execution surface.");
        return await _tokenValidator.ValidateAsync(token, options.AllowedAudiences, ct);
    }
}

public sealed class GoogleExecutionTokenValidator : IGoogleExecutionTokenValidator
{
    public async Task<string> ValidateAsync(string token, IReadOnlySet<string> allowedAudiences, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var payload = await GoogleJsonWebSignature.ValidateAsync(token, new GoogleJsonWebSignature.ValidationSettings { Audience = allowedAudiences });
        if (!payload.EmailVerified || string.IsNullOrWhiteSpace(payload.Email)) throw new ExecutionAccessDeniedException("Google OIDC execution identity must contain a verified email.");
        return payload.Email.Trim();
    }
}
