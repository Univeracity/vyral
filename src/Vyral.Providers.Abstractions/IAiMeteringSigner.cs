using System.Security.Cryptography;

namespace Vyral.Providers.Abstractions;

/// <summary>
/// Caller-owned signing boundary. Cloud KMS/HSM implementations may perform these operations
/// remotely; Vyral never requires private key material to enter a provider adapter.
/// </summary>
public interface IAiMeteringReceiptSigner
{
    string Issuer { get; }
    string KeyId { get; }
    ValueTask SignReceiptAsync(AiMeteringReceipt receipt, CancellationToken ct = default);
}

/// <summary>Review-only signing boundary for an independently operated reviewer.</summary>
public interface IAiMeteringReviewSigner
{
    string Issuer { get; }
    string KeyId { get; }
    ValueTask SignReviewAsync(AiMeteringReview review, CancellationToken ct = default);
}

/// <summary>
/// Convenience composition for local deployments that deliberately use one implementation for
/// both roles. Hosted deployments should normally grant each workload only its narrower interface.
/// </summary>
public interface IAiMeteringSigner : IAiMeteringReceiptSigner, IAiMeteringReviewSigner
{
}

/// <summary>In-process ES256 signer for local evaluation and protected secret-mounted keys.</summary>
public sealed class EcdsaAiMeteringSigner : IAiMeteringSigner, IDisposable
{
    private readonly ECDsa _key;
    private readonly bool _ownsKey;
    private readonly object _sync = new();
    private bool _disposed;

    public EcdsaAiMeteringSigner(ECDsa key, string issuer, string keyId, bool ownsKey = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!AiMeteringCryptography.IsP256(key))
        {
            throw new ArgumentException("ES256 requires a NIST P-256 key.", nameof(key));
        }
        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new ArgumentException("A non-empty issuer is required.", nameof(issuer));
        }
        if (issuer.Length > 512)
        {
            throw new ArgumentException("An issuer cannot exceed 512 characters.", nameof(issuer));
        }
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("A non-empty key id is required.", nameof(keyId));
        }
        if (keyId.Length > 512)
        {
            throw new ArgumentException("A key id cannot exceed 512 characters.", nameof(keyId));
        }

        _key = key;
        _ownsKey = ownsKey;
        Issuer = issuer;
        KeyId = keyId;
    }

    public string Issuer { get; }
    public string KeyId { get; }

    public ValueTask SignReceiptAsync(AiMeteringReceipt receipt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            AiMeteringCryptography.SignReceipt(receipt, _key, Issuer, KeyId);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask SignReviewAsync(AiMeteringReview review, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            AiMeteringCryptography.SignReview(review, _key, Issuer, KeyId);
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_ownsKey)
            {
                _key.Dispose();
            }
        }
    }
}

/// <summary>Receipt-only ES256 signer for a least-privilege runner process.</summary>
public sealed class EcdsaAiMeteringReceiptSigner : IAiMeteringReceiptSigner, IDisposable
{
    private readonly EcdsaAiMeteringSigner _signer;

    public EcdsaAiMeteringReceiptSigner(ECDsa key, string issuer, string keyId, bool ownsKey = false)
    {
        _signer = new EcdsaAiMeteringSigner(key, issuer, keyId, ownsKey);
    }

    public string Issuer => _signer.Issuer;
    public string KeyId => _signer.KeyId;

    public ValueTask SignReceiptAsync(AiMeteringReceipt receipt, CancellationToken ct = default) =>
        _signer.SignReceiptAsync(receipt, ct);

    public void Dispose() => _signer.Dispose();
}

/// <summary>Review-only ES256 signer for a separately trusted reviewer process.</summary>
public sealed class EcdsaAiMeteringReviewSigner : IAiMeteringReviewSigner, IDisposable
{
    private readonly EcdsaAiMeteringSigner _signer;

    public EcdsaAiMeteringReviewSigner(ECDsa key, string issuer, string keyId, bool ownsKey = false)
    {
        _signer = new EcdsaAiMeteringSigner(key, issuer, keyId, ownsKey);
    }

    public string Issuer => _signer.Issuer;
    public string KeyId => _signer.KeyId;

    public ValueTask SignReviewAsync(AiMeteringReview review, CancellationToken ct = default) =>
        _signer.SignReviewAsync(review, ct);

    public void Dispose() => _signer.Dispose();
}
