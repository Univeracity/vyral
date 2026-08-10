using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface ICollectionInspectionService
{
    Task<CollectionInspectionResult> InspectAsync(
        string collection,
        CollectionInspectionRequest request,
        CancellationToken ct = default);
}
