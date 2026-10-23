using Microsoft.AspNetCore.Http;
using Vyral.Server;

namespace Vyral.Tests.Local;

public sealed class RequestCredentialSecurityTests
{
    [Fact]
    public void ApiKeyAccessRejectsConflictingCredentialHeaders()
    {
        var options = new ServerAccessOptions
        {
            RequireApiKey = true,
            ApiKeys = new[] { "trusted-secret" }
        };
        var context = new DefaultHttpContext();
        context.Request.Headers[ServerAccessOptions.DefaultApiKeyHeader] =
            "trusted-secret";
        context.Request.Headers.Authorization = "Bearer conflicting-secret";

        Assert.False(options.IsAuthorized(context.Request));

        context.Request.Headers.Authorization = "Bearer trusted-secret";
        Assert.True(options.IsAuthorized(context.Request));
    }

    [Fact]
    public async Task WorkloadIdentityAuthenticatorsRejectConflictingHeaders()
    {
        var validator = new RecordingGoogleValidator();
        var authenticators = new (Type ErrorType, Func<HttpContext, Task<string>> Authenticate)[]
        {
            (typeof(ExecutionAccessDeniedException), context =>
                new GoogleOidcExecutionIdentityAuthenticator(validator).AuthenticateAsync(
                    context,
                    new VyralExecutionAccessOptions
                    {
                        AllowedAudiences = new HashSet<string> { "audience" }
                    },
                    null!)),
            (typeof(CanonicalAccessDeniedException), context =>
                new GoogleOidcCanonicalIdentityAuthenticator(validator).AuthenticateAsync(
                    context,
                    new VyralCanonicalAccessOptions
                    {
                        AllowedAudiences = new HashSet<string> { "audience" }
                    },
                    null!)),
            (typeof(ObjectAccessDeniedException), context =>
                new GoogleOidcObjectIdentityAuthenticator(validator).AuthenticateAsync(
                    context,
                    new VyralObjectAccessOptions
                    {
                        AllowedAudiences = new HashSet<string> { "audience" }
                    },
                    null!))
        };

        foreach (var (errorType, authenticate) in authenticators)
        {
            var conflicting = new DefaultHttpContext();
            conflicting.Request.Headers["X-Serverless-Authorization"] =
                "Bearer trusted-token";
            conflicting.Request.Headers.Authorization =
                "Bearer conflicting-token";
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => authenticate(conflicting));
            Assert.IsType(errorType, error);

            var matching = new DefaultHttpContext();
            matching.Request.Headers["X-Serverless-Authorization"] =
                "Bearer trusted-token";
            matching.Request.Headers.Authorization = "Bearer trusted-token";
            Assert.Equal("verified@example.test", await authenticate(matching));
        }

        Assert.Equal(3, validator.Tokens.Count);
        Assert.All(validator.Tokens, token => Assert.Equal("trusted-token", token));
    }

    private sealed class RecordingGoogleValidator :
        IGoogleExecutionTokenValidator,
        IGoogleCanonicalTokenValidator
    {
        internal List<string> Tokens { get; } = new();

        public Task<string> ValidateAsync(
            string token,
            IReadOnlySet<string> allowedAudiences,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Contains("audience", allowedAudiences);
            Tokens.Add(token);
            return Task.FromResult("verified@example.test");
        }
    }
}
