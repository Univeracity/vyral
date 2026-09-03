using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Vyral.Providers.Abstractions;
using Vyral.Server;

namespace Vyral.Tests.Local;

public sealed class ProviderMeteringOptionsTests
{
    [Fact]
    public async Task ConfiguredPemSigner_ProducesVerifiableObserverReceipt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-metering-{Guid.NewGuid():N}.pem");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await File.WriteAllTextAsync(path, key.ExportECPrivateKeyPem());
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:Metering:SigningKeyPath"] = path,
                ["Providers:Metering:Issuer"] = "spiffe://vyral.test/runner",
                ["Providers:Metering:KeyId"] = "test-key"
            }).Build();

            var options = ProviderMeteringOptions.FromConfiguration(configuration);
            using var signer = options.CreateSigner();
            Assert.NotNull(signer);
            var receipt = CreateReceipt();
            receipt.AttestationLevel = AiMeteringAttestationLevels.ObserverSigned;
            await signer!.SignReceiptAsync(receipt);

            var verified = AiMeteringCryptography.VerifyReceipt(
                receipt,
                key,
                "spiffe://vyral.test/runner",
                "test-key");
            Assert.True(verified.Valid, string.Join("; ", verified.Errors));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Configuration_RejectsPartialOrMissingSigningConfiguration()
    {
        var partial = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Metering:Issuer"] = "runner-a"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => ProviderMeteringOptions.FromConfiguration(partial).CreateSigner());

        var missingIdentity = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Metering:SigningKeyPath"] = "/not/a/real/key.pem"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => ProviderMeteringOptions.FromConfiguration(missingIdentity).CreateSigner());
    }

    [Fact]
    public async Task ConfiguredPemSigner_RejectsPublicOnlyKeyAtStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-metering-public-{Guid.NewGuid():N}.pem");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await File.WriteAllTextAsync(path, key.ExportSubjectPublicKeyInfoPem());
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:Metering:SigningKeyPath"] = path,
                ["Providers:Metering:Issuer"] = "spiffe://vyral.test/runner",
                ["Providers:Metering:KeyId"] = "public-only"
            }).Build();

            var error = Assert.Throws<InvalidOperationException>(() =>
                ProviderMeteringOptions.FromConfiguration(configuration).CreateSigner());
            Assert.Contains("private key material", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AiMeteringReceipt CreateReceipt() => new()
    {
        Subject = new AiMeteringSubject { ProviderRunId = "run-1" },
        Provider = "provider-a",
        Capability = ProviderCapabilityIds.AiChat,
        Operation = "run",
        Period = new AiMeteringPeriod
        {
            ObservedStartedAt = "2026-09-03T12:00:00.000Z",
            ObservedCompletedAt = "2026-09-03T12:00:00.010Z",
            ElapsedDurationMs = 10
        },
        Completeness = AiMeteringCompleteness.Partial,
        IssuedAt = "2026-09-03T12:00:00.010Z"
    };
}
