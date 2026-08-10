using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public static class RecordSearchProjectionOperations
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";
}

/// <summary>
/// A change from a canonical record store into an eventually-consistent search
/// index. Revisions are mandatory so a replayed or out-of-order stream record
/// cannot replace a newer projection document.
/// </summary>
public sealed class RecordSearchProjectionChange
{
    [JsonPropertyName("collection")]
    public string Collection { get; init; } = string.Empty;

    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public int Revision { get; init; }

    [JsonPropertyName("record")]
    public VyralRecord? Record { get; init; }

    /// <summary>
    /// An opaque upstream sequence for diagnostics only. Correctness is based
    /// on the canonical record revision, not on provider stream ordering.
    /// </summary>
    [JsonPropertyName("sourceSequence")]
    public string? SourceSequence { get; init; }

    public static RecordSearchProjectionChange Upsert(
        string collection,
        VyralRecord record,
        string? sourceSequence = null) => new()
    {
        Collection = collection,
        PartitionKey = record.PartitionKey,
        Id = record.Id,
        Operation = RecordSearchProjectionOperations.Upsert,
        Revision = record.Revision ?? 0,
        Record = record,
        SourceSequence = sourceSequence
    };

    public static RecordSearchProjectionChange Delete(
        string collection,
        string partitionKey,
        string id,
        int revision,
        string? sourceSequence = null) => new()
    {
        Collection = collection,
        PartitionKey = partitionKey,
        Id = id,
        Operation = RecordSearchProjectionOperations.Delete,
        Revision = revision,
        SourceSequence = sourceSequence
    };

    public void Validate()
    {
        RecordIdentityValidator.ValidateCollectionName(Collection);
        RecordIdentityValidator.ValidatePartitionKey(PartitionKey);
        RecordIdentityValidator.ValidateRecordId(Id);

        if (Revision <= 0)
        {
            throw new InvalidOperationException("A search projection change requires a positive canonical record revision.");
        }

        if (Operation is not RecordSearchProjectionOperations.Upsert and not RecordSearchProjectionOperations.Delete)
        {
            throw new InvalidOperationException("A search projection operation must be 'upsert' or 'delete'.");
        }

        if (Operation == RecordSearchProjectionOperations.Upsert)
        {
            if (Record is null)
            {
                throw new InvalidOperationException("An upsert projection change requires its canonical record.");
            }

            RecordIdentityValidator.ValidateRecord(Record);
            if (!string.Equals(PartitionKey, Record.PartitionKey, StringComparison.Ordinal) ||
                !string.Equals(Id, Record.Id, StringComparison.Ordinal) ||
                Record.Revision != Revision)
            {
                throw new InvalidOperationException("The projection change identity and revision must match its canonical record.");
            }
        }
    }
}

/// <summary>
/// An identity returned by an eventual search index. The revision allows the
/// hydrator to discard a stale candidate before it reaches an application.
/// </summary>
public sealed class RecordSearchProjectionCandidate
{
    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public int Revision { get; init; }

    [JsonPropertyName("score")]
    public float Score { get; init; }
}

/// <summary>
/// Candidate output from an optional derived index. Results are deliberately
/// labelled eventual: a caller that needs read-after-write search must use the
/// canonical store or wait for its projection checkpoint.
/// </summary>
public sealed class RecordSearchProjectionResult
{
    [JsonPropertyName("items")]
    public List<RecordSearchProjectionCandidate> Items { get; init; } = new();

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; init; }

    [JsonPropertyName("consistency")]
    public string Consistency { get; init; } = "eventual";
}

public sealed class HydratedRecordSearchProjectionResult
{
    [JsonPropertyName("items")]
    public List<VyralRecordMatch> Items { get; init; } = new();

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; init; }

    [JsonPropertyName("consistency")]
    public string Consistency { get; init; } = "eventual";

    [JsonPropertyName("staleCandidatesDiscarded")]
    public int StaleCandidatesDiscarded { get; init; }
}
