using System.Threading;
using System.Threading.Tasks;

namespace Vyral.Abstractions.Interfaces;

public interface IEmbeddingProvider
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
    string ProviderId { get; }
    int Dimensions { get; }
    string ModelId { get; }
}
