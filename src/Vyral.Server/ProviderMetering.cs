using Vyral.Providers.Abstractions;

namespace Vyral.Server;

internal static class ProviderMetering
{
    public static ProviderRunResult CreateUnhandledFailure(
        string provider,
        ProviderRunRequest request,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);
        return new ProviderRunResult
        {
            Status = ProviderRunStatus.Failed,
            Provider = provider,
            Capability = request.Capability,
            Operation = request.Operation,
            Mode = request.Mode,
            FailureClass = ProviderFailureClasses.Unknown,
            ProviderStatus = "unhandled_exception",
            Error = "Provider execution failed unexpectedly.",
            Trace = new ProviderTraceEvent
            {
                Provider = provider,
                Capability = request.Capability,
                Operation = request.Operation,
                Mode = request.Mode,
                ModelId = request.ModelId,
                AdapterId = "server-provider-runner",
                InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
                FailureClass = ProviderFailureClasses.Unknown
            }
        };
    }

    public static async ValueTask AttachAsync(
        ProviderRunResult result,
        AiMeteringReceipt receipt,
        IAiMeteringReceiptSigner? signer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(result.Metering);
        if (result.Metering.Count >= AiMeteringValidator.MaxReceiptsPerRun)
        {
            throw new InvalidOperationException($"A provider run cannot contain more than {AiMeteringValidator.MaxReceiptsPerRun} metering receipts.");
        }
        foreach (var existing in result.Metering)
        {
            AiMeteringValidator.ValidateReceipt(existing).ThrowIfInvalid();
        }
        if (result.Metering.Any(existing => string.Equals(existing.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A provider run cannot contain duplicate metering receipt identifiers.");
        }
        if (signer is not null)
        {
            receipt.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
            await signer.SignReceiptAsync(receipt, ct);
            AiMeteringValidator.ValidateReceipt(receipt, requireIntegrity: true).ThrowIfInvalid();
        }
        else
        {
            AiMeteringValidator.ValidateReceipt(receipt).ThrowIfInvalid();
        }
        result.Metering.Add(receipt);
    }
}
