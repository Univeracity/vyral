using System.Net;
using System.Net.Http.Json;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

/// <summary>CanonicalStore and HTTP helpers for the versioned EvidenceBrief artifact.</summary>
public static class EvidenceBriefStoreExtensions
{
    public static Task<CanonicalTransactionResult> StoreEvidenceBriefAsync(
        this ICanonicalStore store,
        EvidenceBriefWriteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.CommitAsync(EvidenceBriefContract.CreateUpsertTransaction(request), ct);
    }

    public static async Task<EvidenceBriefDocument?> GetEvidenceBriefAsync(
        this ICanonicalStore store,
        string tenantId,
        string briefId,
        bool includeDeleted = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var document = await store.GetDocumentAsync(tenantId, EvidenceBriefContract.CanonicalDocumentType, briefId, includeDeleted, ct);
        return document is null ? null : EvidenceBriefContract.FromCanonicalDocument(document);
    }

    public static async Task<CanonicalTransactionResult> StoreEvidenceBriefAsync(
        this HttpClient client,
        EvidenceBriefWriteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var transaction = EvidenceBriefContract.CreateUpsertTransaction(request);
        using var response = await client.PostAsJsonAsync(
            $"/canonical/tenants/{Uri.EscapeDataString(transaction.TenantId)}/transactions",
            transaction,
            cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CanonicalTransactionResult>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Vyral EvidenceBrief store response was empty.");
    }

    public static async Task<EvidenceBriefDocument?> GetEvidenceBriefAsync(
        this HttpClient client,
        string tenantId,
        string briefId,
        bool includeDeleted = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var request = new CanonicalDocumentReadRequest
        {
            TenantId = tenantId,
            DocumentType = EvidenceBriefContract.CanonicalDocumentType,
            Id = briefId,
            IncludeDeleted = includeDeleted
        };
        using var response = await client.PostAsJsonAsync(
            $"/canonical/tenants/{Uri.EscapeDataString(tenantId)}/documents/read",
            request,
            cancellationToken: ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<CanonicalDocument>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Vyral EvidenceBrief read response was empty.");
        return EvidenceBriefContract.FromCanonicalDocument(document);
    }
}
