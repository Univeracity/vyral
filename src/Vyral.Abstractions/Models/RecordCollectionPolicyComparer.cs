using System;
using System.Collections.Generic;
using System.Linq;

namespace Vyral.Abstractions.Models;

public static class RecordCollectionPolicyComparer
{
    public static bool AreEquivalent(
        RecordCollectionPolicy? left,
        RecordCollectionPolicy? right,
        bool compareIndexedMetadata = true)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            string.Equals(left.PartitionKeyPath, right.PartitionKeyPath, StringComparison.Ordinal) &&
            VectorPoliciesEquivalent(left.VectorPolicies, right.VectorPolicies) &&
            (!compareIndexedMetadata || SetEquals(left.IndexedMetadata, right.IndexedMetadata));
    }

    private static bool VectorPoliciesEquivalent(IReadOnlyCollection<VectorFieldPolicy> left, IReadOnlyCollection<VectorFieldPolicy> right)
    {
        if (left.Count != right.Count) return false;

        var leftOrdered = left.OrderBy(policy => policy.Name, StringComparer.Ordinal).ThenBy(policy => policy.Path, StringComparer.Ordinal).ToList();
        var rightOrdered = right.OrderBy(policy => policy.Name, StringComparer.Ordinal).ThenBy(policy => policy.Path, StringComparer.Ordinal).ToList();

        for (var i = 0; i < leftOrdered.Count; i++)
        {
            var leftPolicy = leftOrdered[i];
            var rightPolicy = rightOrdered[i];
            if (!string.Equals(leftPolicy.Name, rightPolicy.Name, StringComparison.Ordinal) ||
                !string.Equals(leftPolicy.Path, rightPolicy.Path, StringComparison.Ordinal) ||
                leftPolicy.Dimensions != rightPolicy.Dimensions ||
                !string.Equals(leftPolicy.Datatype, rightPolicy.Datatype, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(leftPolicy.DistanceFunction, rightPolicy.DistanceFunction, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(leftPolicy.IndexType, rightPolicy.IndexType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right)
    {
        return new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);
    }
}
