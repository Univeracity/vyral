using System;
using System.Linq;

namespace Vyral.Abstractions.Models;

public static class RecordVectorValidator
{
    public static void ValidateRecordVectors(string collection, RecordCollectionPolicy policy, VyralRecord record)
    {
        if (record.Vectors == null) return;

        foreach (var (name, vector) in record.Vectors)
        {
            var fieldPolicy = policy.VectorPolicies.FirstOrDefault(p => p.Name == name);
            if (fieldPolicy == null) throw new InvalidOperationException($"Vector field '{name}' is not defined in policy for collection '{collection}'.");
            if (!string.Equals(vector.Datatype, fieldPolicy.Datatype, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Vector field '{name}' datatype '{vector.Datatype}' does not match policy datatype '{fieldPolicy.Datatype}'.");
            if (!string.Equals(vector.DistanceFunction, fieldPolicy.DistanceFunction, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Vector field '{name}' distance function '{vector.DistanceFunction}' does not match policy distance function '{fieldPolicy.DistanceFunction}'.");
            if (vector.Values.Length != fieldPolicy.Dimensions)
                throw new InvalidOperationException($"Vector field '{name}' has dimensions {vector.Values.Length}, but policy expects {fieldPolicy.Dimensions}.");
            if (vector.Dimensions != 0 && vector.Dimensions != fieldPolicy.Dimensions)
                throw new InvalidOperationException($"Vector field '{name}' declares dimensions {vector.Dimensions}, but policy expects {fieldPolicy.Dimensions}.");

            vector.Dimensions = fieldPolicy.Dimensions;
        }
    }

    public static VectorFieldPolicy ValidateSearchVector(string collection, RecordCollectionPolicy policy, VectorSearchOptions vector)
    {
        var fieldPolicy = policy.VectorPolicies.FirstOrDefault(p => p.Name == vector.Field);
        if (fieldPolicy == null) throw new InvalidOperationException($"Vector field '{vector.Field}' is not defined in policy for collection '{collection}'.");
        if (vector.Value.Length != fieldPolicy.Dimensions)
        {
            throw new InvalidOperationException($"Search vector for field '{vector.Field}' has dimensions {vector.Value.Length}, but policy expects {fieldPolicy.Dimensions}.");
        }
        if (vector.Top <= 0)
        {
            throw new InvalidOperationException("Vector search top must be greater than zero.");
        }

        return fieldPolicy;
    }
}
