using System.Text.Json;

namespace Vyral.Tests.Providers;

public class OwnedNativeFailureTests
{
    [Fact]
    public void OwnerFailureVocabularyNeverDisclosesPrivateMessages()
    {
        Assert.Equal("native_binding", OwnedNativeFailure.Classify(new ArgumentException("native_binding")));
        Assert.Equal("owner_profile_refused", OwnedNativeFailure.Classify(new ArgumentException("PRIVATE-SENTINEL")));
        Assert.Equal("owner_profile_invalid", OwnedNativeFailure.Classify(new JsonException("PRIVATE-SENTINEL")));
        Assert.Equal("owner_io_failed", OwnedNativeFailure.Classify(new IOException("PRIVATE-SENTINEL")));
        Assert.Equal("owner_deadline_expired", OwnedNativeFailure.Classify(new OperationCanceledException("PRIVATE-SENTINEL")));
    }
}
