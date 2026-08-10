using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface ITraceStore
{
    Task WriteTraceAsync(TraceRecord trace, CancellationToken ct = default);
    Task<TraceRecord?> GetTraceAsync(string id, CancellationToken ct = default);
    Task<IEnumerable<TraceRecord>> ListTracesAsync(string? operation = null, int? limit = null, CancellationToken ct = default);
    Task<TraceSummary> SummarizeTracesAsync(string? operation = null, CancellationToken ct = default);
    Task<TraceExportBundle> ExportTracesAsync(TraceExportRequest request, CancellationToken ct = default);
    Task<TracePruneResult> PruneTracesAsync(TracePruneRequest request, CancellationToken ct = default);
}
