using System.Security.Cryptography;
using System.Text;
using Vyral.Primitives;

namespace Vyral.Execution;

/// <summary>
/// Projects the durable execution reservation into the public admission contract. The receipt is
/// deterministic, contains no caller secret, and can therefore be reconstructed after restart.
/// </summary>
public static class ExecutionAdmission
{
    public static AdmissionReceipt Attach(
        ExecutionRun run,
        string operationId,
        string statusUri,
        string? resultUri = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        run.Admission = Create(run, operationId, statusUri, resultUri);
        // Runtime stores need the raw key to resolve replays, but public execution resources do
        // not. The receipt carries a one-way hash that is sufficient for caller correlation.
        run.IdempotencyKey = null;
        return run.Admission;
    }

    public static AdmissionReceipt Create(
        ExecutionRun run,
        string operationId,
        string statusUri,
        string? resultUri = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(statusUri);

        var rejected = string.Equals(run.Status, ExecutionRunStatuses.Rejected, StringComparison.Ordinal);
        return new AdmissionReceipt
        {
            AdmissionId = BuildAdmissionId(operationId, run.Id),
            OperationId = operationId,
            Status = rejected ? AdmissionStatuses.Rejected : AdmissionStatuses.Accepted,
            ResourceId = run.Id,
            RequestHash = run.PayloadHash,
            IdempotencyKeyHash = string.IsNullOrWhiteSpace(run.IdempotencyKey)
                ? null
                : Sha256(run.IdempotencyKey),
            Replayed = run.AdmissionReplayed,
            AdmittedAtUtc = run.CreatedAtUtc,
            StatusUri = statusUri,
            ResultUri = resultUri,
            FailureClass = rejected ? run.FailureClass : null,
            Error = rejected ? run.Error : null
        };
    }

    private static string BuildAdmissionId(string operationId, string resourceId) =>
        "adm_" + Sha256(string.Join('\n', AdmissionReceipt.CurrentVersion, operationId, resourceId));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
