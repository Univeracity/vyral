using System.Net;
using Vyral.Providers.Abstractions;

namespace Vyral.Providers.Jules;

public static class JulesFailureClassifier
{
    public static string Classify(HttpStatusCode statusCode, string responseText)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderFailureClasses.Auth;
        }

        if ((int)statusCode == 429)
        {
            var lower = responseText.ToLowerInvariant();
            return lower.Contains("quota") ? ProviderFailureClasses.Quota : ProviderFailureClasses.RateLimit;
        }

        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            return ProviderFailureClasses.Schema;
        }

        if ((int)statusCode >= 500)
        {
            return ProviderFailureClasses.ProviderUnavailable;
        }

        return ProviderFailureClasses.Unknown;
    }
}
