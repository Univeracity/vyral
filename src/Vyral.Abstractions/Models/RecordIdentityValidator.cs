using System;
using System.Linq;
using System.Text;

namespace Vyral.Abstractions.Models;

public static class RecordIdentityValidator
{
    public const int MaxCollectionNameLength = 255;
    public const int MaxRecordIdUtf8Bytes = 1023;
    public const int MaxPartitionKeyUtf8Bytes = 2048;

    public static void ValidateCollectionName(string collection)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection is required.", nameof(collection));
        if (collection.Length > MaxCollectionNameLength)
        {
            throw new InvalidOperationException($"Collection name cannot exceed {MaxCollectionNameLength} characters.");
        }

        if (!IsAsciiLetterOrDigit(collection[0]) || !IsAsciiLetterOrDigit(collection[^1]))
        {
            throw new InvalidOperationException("Collection name must start and end with an ASCII letter or digit.");
        }

        if (!collection.All(c => IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new InvalidOperationException("Collection name can only contain ASCII letters, digits, '-', '_', and '.'.");
        }
    }

    public static void ValidateRecord(VyralRecord record)
    {
        ValidateRecordId(record.Id);
        ValidatePartitionKey(record.PartitionKey);
    }

    public static void ValidateRecordId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Record id is required.", nameof(id));
        if (Encoding.UTF8.GetByteCount(id) > MaxRecordIdUtf8Bytes)
        {
            throw new InvalidOperationException($"Record id cannot exceed {MaxRecordIdUtf8Bytes} UTF-8 bytes.");
        }

        if (id.Contains('/') || id.Contains('\\') || id.Contains('?') || id.Contains('#'))
        {
            throw new InvalidOperationException("Record id cannot contain '/', '\\', '?', or '#'.");
        }
    }

    public static void ValidatePartitionKey(string partitionKey)
    {
        if (string.IsNullOrWhiteSpace(partitionKey)) throw new ArgumentException("Record partitionKey is required.", nameof(partitionKey));
        if (Encoding.UTF8.GetByteCount(partitionKey) > MaxPartitionKeyUtf8Bytes)
        {
            throw new InvalidOperationException($"Record partitionKey cannot exceed {MaxPartitionKeyUtf8Bytes} UTF-8 bytes.");
        }
    }

    private static bool IsAsciiLetterOrDigit(char c)
    {
        return c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9';
    }
}
