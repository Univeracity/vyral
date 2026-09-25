using Google.Apis.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vyral.Abstractions.Models;

namespace Vyral.Server;

/// <summary>
/// Workload identity policy for the public object HTTP routes. A policy grants an
/// operation on one container and on keys below explicit, segment-ended prefixes.
/// </summary>
public sealed class VyralObjectAccess
{
    private const string CallerCacheKey = "vyral.object.caller";
    private readonly IHostEnvironment _environment;
    private readonly IReadOnlyDictionary<string, IObjectIdentityAuthenticator> _authenticators;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<VyralObjectIdentityPolicy>> _policies;

    public VyralObjectAccess(VyralObjectAccessOptions options, IHostEnvironment environment,
        IEnumerable<IObjectIdentityAuthenticator> authenticators)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _authenticators = authenticators?.ToDictionary(item => item.AuthenticationMode, StringComparer.Ordinal)
            ?? throw new ArgumentNullException(nameof(authenticators));
        if (Enabled && !_authenticators.ContainsKey(options.AuthenticationMode))
            throw new InvalidOperationException($"Object identity authentication mode '{options.AuthenticationMode}' is not registered by this host.");
        if (Enabled && options.AuthenticationMode == ObjectAuthenticationModes.DevelopmentHeader && !environment.IsDevelopment())
            throw new InvalidOperationException("Development-header object identity authentication is disabled outside Development.");
        _policies = options.IdentityPolicies.GroupBy(item => item.Principal, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<VyralObjectIdentityPolicy>)group.ToList(), StringComparer.Ordinal);
    }

    public VyralObjectAccessOptions Options { get; }
    public bool Enabled => Options.IdentityPolicies.Count > 0;

    public async Task<string> AuthorizeKeyAsync(HttpContext context, string container, string key,
        string operation, CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(container);
        var normalized = ObjectNameValidator.NormalizeObjectKey(key);
        if (Enabled && !await IsAllowedAsync(context, container, normalized, operation, ct))
            throw new ObjectAccessDeniedException("The verified workload identity is not allowed to access this object.");
        return normalized;
    }

    public void ValidateObjectResult(string container, string key, ObjectInfo? result)
    {
        if (Enabled && result is not null &&
            (result.Container != container || result.Key != key))
            throw new ObjectAccessDeniedException("The object store returned an object outside the authorized key.");
    }

    public async Task<string?> AuthorizeListAsync(HttpContext context, string container, string? prefix,
        CancellationToken ct = default)
    {
        ObjectNameValidator.ValidateContainer(container);
        var normalized = string.IsNullOrEmpty(prefix) ? null : ObjectNameValidator.NormalizeObjectKey(prefix, allowTrailingSlash: true);
        if (Enabled && !await IsAllowedAsync(context, container, normalized, ObjectAccessOperations.List, ct))
            throw new ObjectAccessDeniedException("The verified workload identity is not allowed to list this object prefix.");
        return normalized;
    }

    /// <summary>
    /// A provider continuation token may not be scoped to the requested prefix.
    /// Never return unexpected object metadata to the caller.
    /// </summary>
    public void ValidateListResult(string container, string? prefix, ObjectListResult result)
    {
        if (!Enabled) return;
        if (result.Items.Any(item => item.Container != container ||
            (prefix is not null && !item.Key.StartsWith(prefix, StringComparison.Ordinal))))
            throw new ObjectAccessDeniedException("The object store returned keys outside the authorized list scope.");
    }

    private async Task<bool> IsAllowedAsync(HttpContext context, string container, string? keyOrPrefix,
        string operation, CancellationToken ct)
    {
        if (!context.Items.TryGetValue(CallerCacheKey, out var cached) || cached is not string principal)
        {
            var authenticator = _authenticators[Options.AuthenticationMode];
            principal = await authenticator.AuthenticateAsync(context, Options, _environment, ct);
            context.Items[CallerCacheKey] = principal;
        }
        return _policies.TryGetValue(principal, out var policies) && policies.Any(policy =>
            policy.Container == container && policy.AllowedOperations.Contains(operation) &&
            policy.AllowedKeyPrefixes.Any(prefix => prefix == "*" ||
                (keyOrPrefix is not null && keyOrPrefix.StartsWith(prefix, StringComparison.Ordinal))));
    }
}

public sealed class VyralObjectAccessOptions
{
    public string AuthenticationMode { get; init; } = ObjectAuthenticationModes.GoogleOidc;
    public string DevelopmentIdentityHeader { get; init; } = "X-Vyral-Development-Identity";
    public IReadOnlySet<string> AllowedAudiences { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<VyralObjectIdentityPolicy> IdentityPolicies { get; init; } = [];

    public static VyralObjectAccessOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Server:ObjectAccess");
        var policies = section.GetSection("IdentityPolicies").GetChildren()
            .Concat(section.GetSection("identityPolicies").GetChildren())
            .Select(item => new VyralObjectIdentityPolicy
            {
                Principal = item["Principal"] ?? item["principal"] ?? string.Empty,
                Container = item["Container"] ?? item["container"] ?? string.Empty,
                AllowedKeyPrefixes = ReadValues(item, "AllowedKeyPrefixes", "allowedKeyPrefixes"),
                AllowedOperations = ReadValues(item, "AllowedOperations", "allowedOperations")
            }).ToList();
        var options = new VyralObjectAccessOptions
        {
            AuthenticationMode = section["AuthenticationMode"] ?? section["authenticationMode"] ?? ObjectAuthenticationModes.GoogleOidc,
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
        if (string.IsNullOrWhiteSpace(AuthenticationMode))
            throw new InvalidOperationException("Server:ObjectAccess:AuthenticationMode is required when object identity policies are configured.");
        if (AuthenticationMode == ObjectAuthenticationModes.GoogleOidc && AllowedAudiences.Count == 0)
            throw new InvalidOperationException("Server:ObjectAccess:AllowedAudiences is required for Google OIDC object identity authentication.");
        foreach (var policy in IdentityPolicies)
        {
            if (string.IsNullOrWhiteSpace(policy.Principal))
                throw new InvalidOperationException("Object identity policies require Principal.");
            ObjectNameValidator.ValidateContainer(policy.Container);
            if (policy.AllowedKeyPrefixes.Count == 0 || policy.AllowedOperations.Count == 0)
                throw new InvalidOperationException($"Object identity policy '{policy.Principal}' requires AllowedKeyPrefixes and AllowedOperations.");
            if (policy.AllowedKeyPrefixes.Contains("*") && policy.AllowedKeyPrefixes.Count != 1)
                throw new InvalidOperationException("The object key wildcard '*' must be used by itself.");
            foreach (var prefix in policy.AllowedKeyPrefixes.Where(value => value != "*"))
            {
                if (!prefix.EndsWith("/", StringComparison.Ordinal) ||
                    ObjectNameValidator.NormalizeObjectKey(prefix, allowTrailingSlash: true) != prefix ||
                    prefix[..^1].Split('/').Any(part => part.Length == 0))
                    throw new InvalidOperationException("Object key prefixes must be relative and end at a '/' segment boundary.");
            }
            foreach (var operation in policy.AllowedOperations)
            {
                if (operation is not (ObjectAccessOperations.Read or ObjectAccessOperations.Write or ObjectAccessOperations.List or ObjectAccessOperations.Delete))
                    throw new InvalidOperationException($"Object identity policy '{policy.Principal}' has an unsupported operation '{operation}'.");
            }
        }
    }

    private static IReadOnlySet<string> ReadValues(IConfigurationSection section, string pascalName, string camelName) =>
        new HashSet<string>(section.GetSection(pascalName).GetChildren()
            .Concat(section.GetSection(camelName).GetChildren())
            .Select(item => item.Value).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()), StringComparer.Ordinal);
}

public sealed class VyralObjectIdentityPolicy
{
    public required string Principal { get; init; }
    public required string Container { get; init; }
    public IReadOnlySet<string> AllowedKeyPrefixes { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> AllowedOperations { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public static class ObjectAccessOperations
{
    public const string Read = "read";
    public const string Write = "write";
    public const string List = "list";
    public const string Delete = "delete";
}

public static class ObjectAuthenticationModes
{
    public const string GoogleOidc = "google-oidc";
    public const string DevelopmentHeader = "development-header";
}

public sealed class ObjectAccessDeniedException(string message) : Exception(message);

public interface IObjectIdentityAuthenticator
{
    string AuthenticationMode { get; }
    Task<string> AuthenticateAsync(HttpContext context, VyralObjectAccessOptions options,
        IHostEnvironment environment, CancellationToken ct = default);
}

public sealed class DevelopmentHeaderObjectIdentityAuthenticator : IObjectIdentityAuthenticator
{
    public string AuthenticationMode => ObjectAuthenticationModes.DevelopmentHeader;

    public Task<string> AuthenticateAsync(HttpContext context, VyralObjectAccessOptions options,
        IHostEnvironment environment, CancellationToken ct = default)
    {
        if (!environment.IsDevelopment()) throw new ObjectAccessDeniedException("Development object identity is disabled outside Development.");
        var principal = context.Request.Headers[options.DevelopmentIdentityHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(principal)) throw new ObjectAccessDeniedException("An object workload identity is required.");
        return Task.FromResult(principal);
    }
}

public sealed class GoogleOidcObjectIdentityAuthenticator : IObjectIdentityAuthenticator
{
    private readonly IGoogleCanonicalTokenValidator _validator;
    public GoogleOidcObjectIdentityAuthenticator(IGoogleCanonicalTokenValidator? validator = null) =>
        _validator = validator ?? new GoogleCanonicalTokenValidator();
    public string AuthenticationMode => ObjectAuthenticationModes.GoogleOidc;

    public async Task<string> AuthenticateAsync(HttpContext context, VyralObjectAccessOptions options,
        IHostEnvironment environment, CancellationToken ct = default)
    {
        var token = context.Request.Headers["X-Serverless-Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(token)) token = context.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!token.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            throw new ObjectAccessDeniedException("A Google OIDC identity token is required for object storage.");
        try
        {
            return await _validator.ValidateAsync(token[bearer.Length..].Trim(), options.AllowedAudiences, ct);
        }
        catch (CanonicalAccessDeniedException ex)
        {
            throw new ObjectAccessDeniedException(ex.Message);
        }
        catch (InvalidJwtException)
        {
            throw new ObjectAccessDeniedException("Google OIDC object identity token is invalid.");
        }
    }
}
