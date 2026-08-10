using Google.Apis.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vyral.Abstractions.Models;

namespace Vyral.Server;

/// <summary>
/// Tenant authorization for CanonicalStore. This deliberately has an authentication seam: the
/// policy concerns stable workload principals and tenant operations, not a cloud-specific token.
/// </summary>
public sealed class VyralCanonicalAccess
{
    private const string CallerCacheKey = "vyral.canonical.caller";
    private readonly IHostEnvironment _environment;
    private readonly IReadOnlyDictionary<string, ICanonicalIdentityAuthenticator> _authenticators;
    private readonly Dictionary<string, IReadOnlyList<VyralCanonicalIdentityPolicy>> _policies;

    public VyralCanonicalAccess(VyralCanonicalAccessOptions options, IHostEnvironment environment)
        : this(options, environment, [new DevelopmentHeaderCanonicalIdentityAuthenticator(), new GoogleOidcCanonicalIdentityAuthenticator()])
    {
    }

    public VyralCanonicalAccess(VyralCanonicalAccessOptions options, IHostEnvironment environment, IEnumerable<ICanonicalIdentityAuthenticator> authenticators)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _authenticators = authenticators?.ToDictionary(item => item.AuthenticationMode, StringComparer.Ordinal)
            ?? throw new ArgumentNullException(nameof(authenticators));
        if (!Enabled && !_environment.IsDevelopment())
            throw new InvalidOperationException("CanonicalStore endpoints require explicit Server:CanonicalAccess:IdentityPolicies outside Development.");
        if (Enabled && !_authenticators.ContainsKey(Options.AuthenticationMode))
            throw new InvalidOperationException($"Canonical identity authentication mode '{Options.AuthenticationMode}' is not registered by this host.");
        _policies = Options.IdentityPolicies.GroupBy(item => item.Principal, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<VyralCanonicalIdentityPolicy>)group.ToList(), StringComparer.Ordinal);
    }

    public VyralCanonicalAccessOptions Options { get; }
    public bool Enabled => Options.IdentityPolicies.Count > 0;

    public async Task AuthorizeTenantAsync(HttpContext context, string tenantId, string operation, CancellationToken ct = default)
    {
        if (!Enabled) return;
        CanonicalContractValidator.ValidateTenantId(tenantId);
        var caller = await ResolveAsync(context, ct);
        if (!GetPolicies(caller.Principal).Any(policy => policy.AllowedOperations.Contains(operation) && IsTenantAllowed(policy, tenantId)))
            throw new CanonicalAccessDeniedException($"The verified workload identity is not allowed to {operation} canonical tenant '{tenantId}'.");
    }

    public async Task AuthorizeAdminAsync(HttpContext context, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var caller = await ResolveAsync(context, ct);
        if (!GetPolicies(caller.Principal).Any(policy => policy.AllowedOperations.Contains(CanonicalAccessOperations.Admin)))
            throw new CanonicalAccessDeniedException("The verified workload identity is not allowed to administer canonical storage.");
    }

    /// <summary>Returns the verified workload principal for durable canonical audit metadata.</summary>
    public async Task<string?> GetVerifiedPrincipalAsync(HttpContext context, CancellationToken ct = default) =>
        Enabled ? (await ResolveAsync(context, ct)).Principal : null;

    private async Task<VyralCanonicalCaller> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Items.TryGetValue(CallerCacheKey, out var cached) && cached is VyralCanonicalCaller caller) return caller;
        if (!_authenticators.TryGetValue(Options.AuthenticationMode, out var authenticator))
            throw new InvalidOperationException($"Canonical identity authentication mode '{Options.AuthenticationMode}' is not registered by this host.");
        var principal = await authenticator.AuthenticateAsync(context, Options, _environment, ct);
        if (!_policies.ContainsKey(principal)) throw new CanonicalAccessDeniedException("The verified workload identity is not configured for CanonicalStore.");
        caller = new VyralCanonicalCaller { Principal = principal };
        context.Items[CallerCacheKey] = caller;
        return caller;
    }

    private IReadOnlyList<VyralCanonicalIdentityPolicy> GetPolicies(string principal) => _policies.TryGetValue(principal, out var policies) ? policies : Array.Empty<VyralCanonicalIdentityPolicy>();
    private static bool IsTenantAllowed(VyralCanonicalIdentityPolicy policy, string tenantId) =>
        policy.AllowedTenantIds.Contains("*") || policy.AllowedTenantIds.Contains(tenantId);
}

public sealed class VyralCanonicalAccessOptions
{
    public string AuthenticationMode { get; init; } = VyralCanonicalAuthenticationModes.GoogleOidc;
    public string DevelopmentIdentityHeader { get; init; } = "X-Vyral-Development-Identity";
    public IReadOnlySet<string> AllowedAudiences { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<VyralCanonicalIdentityPolicy> IdentityPolicies { get; init; } = Array.Empty<VyralCanonicalIdentityPolicy>();

    public static VyralCanonicalAccessOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Server:CanonicalAccess");
        var policies = section.GetSection("IdentityPolicies").GetChildren().Concat(section.GetSection("identityPolicies").GetChildren())
            .Select(item => new VyralCanonicalIdentityPolicy
            {
                Principal = item["Principal"] ?? item["principal"] ?? string.Empty,
                AllowedTenantIds = ReadValues(item, "AllowedTenantIds", "allowedTenantIds"),
                AllowedOperations = ReadValues(item, "AllowedOperations", "allowedOperations")
            }).ToList();
        var options = new VyralCanonicalAccessOptions
        {
            AuthenticationMode = section["AuthenticationMode"] ?? section["authenticationMode"] ?? VyralCanonicalAuthenticationModes.GoogleOidc,
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
        if (string.IsNullOrWhiteSpace(AuthenticationMode)) throw new InvalidOperationException("Server:CanonicalAccess:AuthenticationMode is required when canonical identity policies are configured.");
        if (string.Equals(AuthenticationMode, VyralCanonicalAuthenticationModes.GoogleOidc, StringComparison.Ordinal) && AllowedAudiences.Count == 0)
            throw new InvalidOperationException("Server:CanonicalAccess:AllowedAudiences is required for Google OIDC canonical identity authentication.");
        foreach (var policy in IdentityPolicies)
        {
            if (string.IsNullOrWhiteSpace(policy.Principal)) throw new InvalidOperationException("Canonical identity policies require Principal.");
            if (policy.AllowedOperations.Count == 0) throw new InvalidOperationException($"Canonical identity policy '{policy.Principal}' must declare AllowedOperations.");
            if (policy.AllowedTenantIds.Count == 0) throw new InvalidOperationException($"Canonical identity policy '{policy.Principal}' must declare AllowedTenantIds or the explicit '*' wildcard.");
            if (policy.AllowedTenantIds.Contains("*") && policy.AllowedTenantIds.Count != 1)
                throw new InvalidOperationException($"Canonical identity policy '{policy.Principal}' must use '*' by itself when authorizing every tenant.");
            foreach (var tenantId in policy.AllowedTenantIds)
            {
                if (tenantId != "*") CanonicalContractValidator.ValidateTenantId(tenantId);
            }
            foreach (var operation in policy.AllowedOperations)
            {
                if (operation is not (CanonicalAccessOperations.Read or CanonicalAccessOperations.Write or CanonicalAccessOperations.Dispatch or CanonicalAccessOperations.Export or CanonicalAccessOperations.Restore or CanonicalAccessOperations.Admin))
                    throw new InvalidOperationException($"Canonical identity policy '{policy.Principal}' has an unsupported operation '{operation}'.");
            }
            if (policy.AllowedOperations.Contains(CanonicalAccessOperations.Admin) && !policy.AllowedTenantIds.Contains("*"))
                throw new InvalidOperationException($"Canonical identity policy '{policy.Principal}' must use the explicit '*' tenant wildcard for the global admin operation.");
        }
    }

    private static IReadOnlySet<string> ReadValues(IConfigurationSection section, string pascalName, string camelName) => new HashSet<string>(section.GetSection(pascalName).GetChildren()
        .Concat(section.GetSection(camelName).GetChildren()).Select(item => item.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()), StringComparer.Ordinal);
}

public sealed class VyralCanonicalIdentityPolicy
{
    public required string Principal { get; init; }
    public IReadOnlySet<string> AllowedTenantIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> AllowedOperations { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public static class CanonicalAccessOperations
{
    public const string Read = "read";
    public const string Write = "write";
    public const string Dispatch = "dispatch";
    public const string Export = "export";
    public const string Restore = "restore";
    public const string Admin = "admin";
}

public static class VyralCanonicalAuthenticationModes
{
    public const string GoogleOidc = "google-oidc";
    public const string DevelopmentHeader = "development-header";
}

public sealed class VyralCanonicalCaller { public required string Principal { get; init; } }
public sealed class CanonicalAccessDeniedException(string message) : Exception(message);

public interface ICanonicalIdentityAuthenticator
{
    string AuthenticationMode { get; }
    Task<string> AuthenticateAsync(HttpContext context, VyralCanonicalAccessOptions options, IHostEnvironment environment, CancellationToken ct = default);
}

public sealed class DevelopmentHeaderCanonicalIdentityAuthenticator : ICanonicalIdentityAuthenticator
{
    public string AuthenticationMode => VyralCanonicalAuthenticationModes.DevelopmentHeader;

    public Task<string> AuthenticateAsync(HttpContext context, VyralCanonicalAccessOptions options, IHostEnvironment environment, CancellationToken ct = default)
    {
        if (!environment.IsDevelopment()) throw new CanonicalAccessDeniedException("Development-header canonical identity authentication is disabled outside Development.");
        var principal = context.Request.Headers[options.DevelopmentIdentityHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(principal)) throw new CanonicalAccessDeniedException("A development canonical identity header is required.");
        return Task.FromResult(principal);
    }
}

public sealed class GoogleOidcCanonicalIdentityAuthenticator : ICanonicalIdentityAuthenticator
{
    private readonly IGoogleCanonicalTokenValidator _tokenValidator;
    public GoogleOidcCanonicalIdentityAuthenticator(IGoogleCanonicalTokenValidator? tokenValidator = null) => _tokenValidator = tokenValidator ?? new GoogleCanonicalTokenValidator();
    public string AuthenticationMode => VyralCanonicalAuthenticationModes.GoogleOidc;

    public async Task<string> AuthenticateAsync(HttpContext context, VyralCanonicalAccessOptions options, IHostEnvironment environment, CancellationToken ct = default)
    {
        if (options.AllowedAudiences.Count == 0) throw new InvalidOperationException("Server:CanonicalAccess:AllowedAudiences is required for Google OIDC canonical identity authentication.");
        var token = GetBearerToken(context.Request);
        if (string.IsNullOrWhiteSpace(token)) throw new CanonicalAccessDeniedException("A Google OIDC identity token is required for CanonicalStore.");
        return await _tokenValidator.ValidateAsync(token, options.AllowedAudiences, ct);
    }

    private static string? GetBearerToken(HttpRequest request)
    {
        var candidate = request.Headers["X-Serverless-Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(candidate)) candidate = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate[prefix.Length..].Trim() : null;
    }
}

public interface IGoogleCanonicalTokenValidator
{
    Task<string> ValidateAsync(string token, IReadOnlySet<string> allowedAudiences, CancellationToken ct = default);
}

public sealed class GoogleCanonicalTokenValidator : IGoogleCanonicalTokenValidator
{
    public async Task<string> ValidateAsync(string token, IReadOnlySet<string> allowedAudiences, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var payload = await GoogleJsonWebSignature.ValidateAsync(token, new GoogleJsonWebSignature.ValidationSettings { Audience = allowedAudiences });
        if (!payload.EmailVerified || string.IsNullOrWhiteSpace(payload.Email)) throw new CanonicalAccessDeniedException("Google OIDC canonical identity must contain a verified email.");
        return payload.Email.Trim();
    }
}
