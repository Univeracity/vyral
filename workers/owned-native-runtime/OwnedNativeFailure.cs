using System.Text.Json;
using Vyral.Providers.Local;

static class OwnedNativeFailure
{
    public static string Classify(Exception error)
    {
        // Only owner-authored literal codes cross this boundary, never arbitrary messages.
        var closed = new[] { "profile_limit", "duplicate_profile_key", "unsupported_profile", "invalid_pin",
            "pin_mismatch", "native_binding", "runtime_manifest", "native_context_limit", "owner_lifetime_unavailable",
            "invalid_native_listener", "invalid_native_resource_limits" };
        if (error is ArgumentException or InvalidOperationException && closed.Contains(error.Message, StringComparer.Ordinal))
            return error.Message;
        if (error is LocalScoringException native && new[] { "model_digest_mismatch", "runtime_binding_mismatch",
            "template_probe_mismatch", "runtime_response_limit", "invalid_runtime_response" }.Contains(native.Reason, StringComparer.Ordinal))
            return native.Reason;
        return error switch
        {
            OperationCanceledException => "owner_deadline_expired",
            JsonException => "owner_profile_invalid",
            IOException or UnauthorizedAccessException => "owner_io_failed",
            _ => "owner_profile_refused"
        };
    }
}
