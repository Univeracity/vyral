using System.Security.Cryptography;
using System.Text.Json;
using Vyral.Providers.Abstractions;

namespace Vyral.Tests.Conformance;

public sealed class PortableAiMeteringGoldenFixtureTests
{
    [Fact]
    public void ReceiptMatchesPortableCanonicalHashes()
    {
        using var manifest = JsonDocument.Parse(ReadResource("Vyral.Tests.Conformance.ai-metering-v1-manifest.json"));
        var receipt = JsonSerializer.Deserialize<AiMeteringReceipt>(
            ReadResource("Vyral.Tests.Conformance.ai-metering-v1-receipt.json"),
            ProviderJson.Options);
        Assert.NotNull(receipt);
        var validation = AiMeteringValidator.ValidateReceipt(receipt!);
        Assert.True(validation.Valid, string.Join("; ", validation.Errors));
        var expectedPayloadHash = manifest.RootElement.GetProperty("expectedPayloadHash").GetString();
        var actualPayloadHash = AiMeteringCryptography.ComputeReceiptPayloadHash(receipt);
        Assert.True(expectedPayloadHash == actualPayloadHash, $"Expected payload hash {expectedPayloadHash}, actual {actualPayloadHash}.");
        var expectedEnvelopeHash = manifest.RootElement.GetProperty("expectedEnvelopeHash").GetString();
        var actualEnvelopeHash = AiMeteringCryptography.ComputeReceiptEnvelopeHash(receipt);
        Assert.True(expectedEnvelopeHash == actualEnvelopeHash, $"Expected envelope hash {expectedEnvelopeHash}, actual {actualEnvelopeHash}.");
        var signatureFixture = manifest.RootElement.GetProperty("signature").GetProperty("fixture");
        var signingRequest = AiMeteringCryptography.CreateReceiptSigningRequest(
            receipt,
            signatureFixture.GetProperty("issuer").GetString()!,
            signatureFixture.GetProperty("keyId").GetString()!);
        var signingInputHash = "sha256:" + Convert.ToHexString(SHA256.HashData(signingRequest.Data.Span)).ToLowerInvariant();
        Assert.Equal(signatureFixture.GetProperty("expectedInputHash").GetString(), signingInputHash);
        using var publicKey = ReadPublicKey(signatureFixture.GetProperty("publicKeyJwk"));
        var signatureVerification = AiMeteringCryptography.VerifyReceipt(
            receipt,
            publicKey,
            signatureFixture.GetProperty("issuer").GetString(),
            signatureFixture.GetProperty("keyId").GetString());
        Assert.True(signatureVerification.Valid, string.Join("; ", signatureVerification.Errors));
    }

    [Fact]
    public void ReviewMatchesPortableCanonicalHashes()
    {
        using var manifest = JsonDocument.Parse(ReadResource("Vyral.Tests.Conformance.ai-metering-v1-manifest.json"));
        var review = JsonSerializer.Deserialize<AiMeteringReview>(
            ReadResource("Vyral.Tests.Conformance.ai-metering-v1-review.json"),
            ProviderJson.Options);
        Assert.NotNull(review);
        var validation = AiMeteringValidator.ValidateReview(review!);
        Assert.True(validation.Valid, string.Join("; ", validation.Errors));
        var expectedPayloadHash = manifest.RootElement.GetProperty("expectedReviewPayloadHash").GetString();
        var actualPayloadHash = AiMeteringCryptography.ComputeReviewPayloadHash(review);
        Assert.True(expectedPayloadHash == actualPayloadHash, $"Expected review payload hash {expectedPayloadHash}, actual {actualPayloadHash}.");
        var expectedEnvelopeHash = manifest.RootElement.GetProperty("expectedReviewEnvelopeHash").GetString();
        var actualEnvelopeHash = AiMeteringCryptography.ComputeReviewEnvelopeHash(review);
        Assert.True(expectedEnvelopeHash == actualEnvelopeHash, $"Expected review envelope hash {expectedEnvelopeHash}, actual {actualEnvelopeHash}.");
        var signatureFixture = manifest.RootElement.GetProperty("reviewSignatureFixture");
        var signingRequest = AiMeteringCryptography.CreateReviewSigningRequest(
            review,
            signatureFixture.GetProperty("issuer").GetString()!,
            signatureFixture.GetProperty("keyId").GetString()!);
        var signingInputHash = "sha256:" + Convert.ToHexString(SHA256.HashData(signingRequest.Data.Span)).ToLowerInvariant();
        Assert.Equal(signatureFixture.GetProperty("expectedInputHash").GetString(), signingInputHash);
        using var publicKey = ReadPublicKey(signatureFixture.GetProperty("publicKeyJwk"));
        var signatureVerification = AiMeteringCryptography.VerifyReview(
            review,
            publicKey,
            signatureFixture.GetProperty("issuer").GetString(),
            signatureFixture.GetProperty("keyId").GetString());
        Assert.True(signatureVerification.Valid, string.Join("; ", signatureVerification.Errors));

        var receipt = JsonSerializer.Deserialize<AiMeteringReceipt>(
            ReadResource("Vyral.Tests.Conformance.ai-metering-v1-receipt.json"),
            ProviderJson.Options)!;
        var receiptFixture = manifest.RootElement.GetProperty("signature").GetProperty("fixture");
        using var receiptKey = ReadPublicKey(receiptFixture.GetProperty("publicKeyJwk"));
        var bundleVerification = AiMeteringReviewer.VerifyReviewBundle(
            review,
            new[] { receipt },
            candidate => AiMeteringCryptography.VerifyReview(candidate, publicKey, signatureFixture.GetProperty("issuer").GetString(), signatureFixture.GetProperty("keyId").GetString()),
            candidate => AiMeteringCryptography.VerifyReceipt(candidate, receiptKey, receiptFixture.GetProperty("issuer").GetString(), receiptFixture.GetProperty("keyId").GetString()));
        Assert.True(bundleVerification.Valid, string.Join("; ", bundleVerification.Errors));
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PortableAiMeteringGoldenFixtureTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded conformance resource '{name}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static ECDsa ReadPublicKey(JsonElement jwk)
    {
        static byte[] Decode(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            return Convert.FromBase64String(normalized);
        }

        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Decode(jwk.GetProperty("x").GetString()!),
                Y = Decode(jwk.GetProperty("y").GetString()!)
            }
        });
    }
}
