namespace Vyral.Tests.Google;

public class FirestoreRecordCollectionStoreTests
{
    [Fact]
    public void FirestoreDocumentIds_AreDeterministicAndFirestoreSafe()
    {
        var policyId = FirestoreRecordCollectionStore.BuildPolicyDocumentId("consumer-results");
        var recordId = FirestoreRecordCollectionStore.BuildRecordDocumentId("consumer-results", "consumer-a", "result-1");

        Assert.Equal(policyId, FirestoreRecordCollectionStore.BuildPolicyDocumentId("consumer-results"));
        Assert.Equal(recordId, FirestoreRecordCollectionStore.BuildRecordDocumentId("consumer-results", "consumer-a", "result-1"));
        Assert.NotEqual(recordId, FirestoreRecordCollectionStore.BuildRecordDocumentId("consumer-results", "consumer-a", "result-2"));
        Assert.DoesNotContain("/", policyId, StringComparison.Ordinal);
        Assert.DoesNotContain("/", recordId, StringComparison.Ordinal);
        Assert.DoesNotContain("+", policyId, StringComparison.Ordinal);
        Assert.DoesNotContain("+", recordId, StringComparison.Ordinal);
        Assert.DoesNotContain("=", policyId, StringComparison.Ordinal);
        Assert.DoesNotContain("=", recordId, StringComparison.Ordinal);
    }
}
