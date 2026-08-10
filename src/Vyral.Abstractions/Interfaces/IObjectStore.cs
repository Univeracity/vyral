using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface IObjectStore
{
    Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default);
    Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default);
    Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default);
    Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default);
}
