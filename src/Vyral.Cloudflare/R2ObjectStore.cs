using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Aws;

namespace Vyral.Cloudflare;

public sealed class R2ObjectStore : IObjectStore, IDisposable
{
    private readonly S3ObjectStore _inner;
    private readonly IAmazonS3? _ownedClient;

    public R2ObjectStore(IAmazonS3 client)
        : this(client, new S3ObjectStoreOptions
        {
            DisablePutObjectPayloadSigning = true,
            DisablePutObjectDefaultChecksumValidation = true
        }, ownsClient: false)
    {
    }

    private R2ObjectStore(IAmazonS3 client, S3ObjectStoreOptions options, bool ownsClient)
    {
        _inner = new S3ObjectStore(client, options);
        _ownedClient = ownsClient ? client : null;
    }

    public static R2ObjectStore Create(CloudflareR2Options options)
    {
        options.ValidateCredentials();

        AWSCredentials credentials = string.IsNullOrWhiteSpace(options.SessionToken)
            ? new BasicAWSCredentials(options.AccessKeyId!, options.SecretAccessKey!)
            : new SessionAWSCredentials(options.AccessKeyId!, options.SecretAccessKey!, options.SessionToken);
        var client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = options.ResolveServiceUrl(),
            AuthenticationRegion = string.IsNullOrWhiteSpace(options.AuthenticationRegion)
                ? "auto"
                : options.AuthenticationRegion.Trim(),
            ForcePathStyle = options.ForcePathStyle,
            MaxErrorRetry = options.MaxErrorRetry
        });

        return new R2ObjectStore(client, new S3ObjectStoreOptions
        {
            DisablePutObjectPayloadSigning = options.DisablePutObjectPayloadSigning,
            DisablePutObjectDefaultChecksumValidation = options.DisablePutObjectDefaultChecksumValidation
        }, ownsClient: true);
    }

    public Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default) =>
        _inner.PutObjectAsync(request, ct);

    public Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default) =>
        _inner.GetObjectAsync(request, ct);

    public Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        // The qualified R2 S3 endpoint ignored DeleteObject If-Match. A HEAD followed
        // by DELETE would race, so never substitute that for an atomic precondition.
        if (!string.IsNullOrWhiteSpace(request.IfMatch))
            throw new InvalidOperationException("Conditional object deletion is unsupported by the R2 profile.");
        return _inner.DeleteObjectAsync(request, ct);
    }

    public Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default) =>
        _inner.ListObjectsAsync(request, ct);

    public void Dispose()
    {
        _ownedClient?.Dispose();
    }
}
