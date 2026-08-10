using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vyral.Abstractions.Models;

namespace Vyral.Aws;

/// <summary>
/// Options for the optional OpenSearch-derived retrieval projection. The
/// destination and its transport security are owned solely by the supplied
/// <see cref="AwsSigV4OpenSearchTransport"/>; this type controls only
/// projection behavior.
/// </summary>
public sealed class OpenSearchRecordSearchProjectionOptions
{
    private const int MaximumIndexNameLength = 255;
    private const string DefaultIndexPrefix = "vyral-";

    /// <summary>
    /// Legacy collection-only resolver for an existing fixed index naming convention. It cannot
    /// distinguish mapping generations; use <see cref="PolicyIndexNameFactory"/> for new
    /// deployments that control their own generation names.
    /// </summary>
    public Func<string, string>? IndexNameFactory { get; init; }

    /// <summary>
    /// Optional policy-aware resolver for an index-generation rollout. When a collection's vector
    /// or filter mapping changes, return a new name, backfill it, then retire the prior derived
    /// index after readers have moved. This takes precedence over <see cref="IndexNameFactory"/>.
    /// Without either resolver, Vyral derives a stable name from the collection and its relevant
    /// mapping shape, so a mapping change already selects a new derived index.
    /// </summary>
    public Func<RecordCollectionPolicy, string>? PolicyIndexNameFactory { get; init; }

    /// <summary>Maximum OpenSearch candidates accepted for one request.</summary>
    public int MaximumCandidates { get; init; } = 1_000;

    internal string GetIndexName(RecordCollectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return ValidateIndexName(
            PolicyIndexNameFactory?.Invoke(policy)
            ?? IndexNameFactory?.Invoke(policy.Name)
            ?? DefaultIndexName(policy));
    }

    internal string GetIndexName(string collection)
    {
        if (PolicyIndexNameFactory is not null || IndexNameFactory is null)
        {
            throw new InvalidOperationException(
                "OpenSearch index resolution requires the canonical collection policy. " +
                "Configure a policy store or call EnsureCollectionAsync before processing a deletion. " +
                "Only an explicitly configured legacy collection-only resolver can route a deletion without policy state.");
        }

        return ValidateIndexName(IndexNameFactory(collection));
    }

    private static string ValidateIndexName(string? index)
    {
        if (string.IsNullOrWhiteSpace(index) || index.Length > MaximumIndexNameLength ||
            !char.IsLetterOrDigit(index[0]) ||
            index.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new InvalidOperationException(
                "An OpenSearch projection index name must start with an ASCII letter or digit and contain only lowercase-safe letters, digits, and '-'.");
        }

        return index.ToLowerInvariant();
    }

    private static string DefaultIndexName(RecordCollectionPolicy policy)
    {
        var collection = policy.Name;
        var normalized = new StringBuilder(collection.Length);
        foreach (var c in collection.ToLowerInvariant())
        {
            normalized.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        var signature = new StringBuilder(collection);
        signature.Append("\nfilters:");
        foreach (var path in policy.IndexedMetadata.OrderBy(path => path, StringComparer.Ordinal))
            signature.Append(path).Append('\n');
        signature.Append("vectors:");
        foreach (var vector in policy.VectorPolicies
                     .OrderBy(vector => vector.Name, StringComparer.Ordinal)
                     .ThenBy(vector => vector.Path, StringComparer.Ordinal))
        {
            signature.Append(vector.Name).Append('\u001f')
                .Append(vector.Path).Append('\u001f')
                .Append(vector.Dimensions).Append('\u001f')
                .Append(vector.Datatype).Append('\u001f')
                .Append(vector.DistanceFunction).Append('\u001f')
                .Append(vector.IndexType).Append('\n');
        }

        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())))[..20].ToLowerInvariant();
        var maximumCollectionPartLength = MaximumIndexNameLength - DefaultIndexPrefix.Length - suffix.Length - 1;
        var collectionPart = normalized.ToString().Trim('-');
        if (collectionPart.Length > maximumCollectionPartLength)
            collectionPart = collectionPart[..maximumCollectionPartLength].TrimEnd('-');
        return $"{DefaultIndexPrefix}{collectionPart}-{suffix}";
    }
}
