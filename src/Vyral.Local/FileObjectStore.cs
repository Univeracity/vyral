using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class FileObjectStore : IObjectStore
{
    private const int DefaultObjectListLimit = 100;
    private const int MaxObjectListLimit = 5000;
    private const string MetadataSuffix = ".metadata.json";
    private const string TempDirectoryName = ".vyral-tmp";
    private readonly string _rootPath;
    private readonly string _lockRootPath;

    public FileObjectStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("Root path is required.", nameof(rootPath));
        _rootPath = Path.GetFullPath(rootPath);
        _lockRootPath = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".vyral-locks";
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_lockRootPath);
    }

    public async Task<ObjectInfo> PutObjectAsync(ObjectWriteRequest request, CancellationToken ct = default)
    {
        ValidateWriteRequest(request);
        var fullPath = ResolvePath(request.Container, request.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var objectLock = await AcquireObjectLockAsync(fullPath, ct);

        var existing = await ResolveExistingInfoAsync(request.Container, request.Key, ct);
        ValidateWritePreconditions(request, existing);

        var tempPath = CreateTempPath();
        try
        {
            await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await request.Content.CopyToAsync(file, ct);
                await file.FlushAsync(ct);
            }

            var tempInfo = new FileInfo(tempPath);
            var contentHash = await ComputeSha256Async(tempPath, ct);
            var info = new ObjectInfo
            {
                Container = request.Container,
                Key = request.Key,
                ContentType = request.ContentType,
                ContentLength = tempInfo.Length,
                ContentHash = contentHash,
                Etag = contentHash,
                Metadata = request.Metadata ?? new Dictionary<string, string>(),
                UpdatedAt = DateTime.UtcNow
            };

            File.Move(tempPath, fullPath, overwrite: true);
            await WriteInfoAsync(info, ct);
            return info;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public async Task<ObjectResult?> GetObjectAsync(ObjectReadRequest request, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(request.Container, request.Key);
        await using var objectLock = await AcquireObjectLockAsync(fullPath, ct);
        if (!File.Exists(fullPath)) return null;

        var info = await ResolveExistingInfoAsync(request.Container, request.Key, ct)
            ?? throw new InvalidOperationException("Object content exists but metadata could not be resolved.");

        return new ObjectResult
        {
            Container = info.Container,
            Key = info.Key,
            ContentType = info.ContentType,
            ContentLength = info.ContentLength,
            ContentHash = info.ContentHash,
            Etag = info.Etag,
            Metadata = info.Metadata,
            UpdatedAt = info.UpdatedAt,
            Content = File.OpenRead(fullPath)
        };
    }

    public async Task DeleteObjectAsync(ObjectDeleteRequest request, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(request.Container, request.Key);
        await using var objectLock = await AcquireObjectLockAsync(fullPath, ct);
        var existing = await ResolveExistingInfoAsync(request.Container, request.Key, ct);
        if (existing == null && !File.Exists(fullPath)) return;

        if (!string.IsNullOrWhiteSpace(request.IfMatch) && !EtagMatches(request.IfMatch, existing?.Etag))
        {
            throw new InvalidOperationException("Object delete precondition failed: ifMatch did not match the current etag.");
        }

        if (File.Exists(fullPath)) File.Delete(fullPath);

        var metadataPath = GetMetadataPath(fullPath);
        if (File.Exists(metadataPath)) File.Delete(metadataPath);
    }

    public async Task<ObjectListResult> ListObjectsAsync(ObjectListRequest request, CancellationToken ct = default)
    {
        var limit = ValidateListLimit(request.Limit);
        ValidateContainer(request.Container);
        var normalizedPrefix = string.IsNullOrEmpty(request.Prefix)
            ? string.Empty
            : NormalizeObjectKey(request.Prefix, allowTrailingSlash: true);

        var containerPath = ResolveContainerPath(request.Container);
        if (!Directory.Exists(containerPath))
        {
            return new ObjectListResult();
        }

        var allKeys = Directory.EnumerateFiles(containerPath, "*", SearchOption.AllDirectories)
            .Where(path => !IsMetadataSidecar(path))
            .Select(path => Path.GetRelativePath(containerPath, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => path.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var start = DecodeContinuationToken(request.ContinuationToken);
        var pageKeys = allKeys.Skip(start).Take(limit).ToList();
        var items = new List<ObjectInfo>();
        foreach (var key in pageKeys)
        {
            var info = await ResolveExistingInfoAsync(request.Container, key, ct);
            if (info != null) items.Add(info);
        }

        var next = start + pageKeys.Count < allKeys.Count ? EncodeContinuationToken(start + pageKeys.Count) : null;
        return new ObjectListResult { Items = items, ContinuationToken = next };
    }

    public Task<FileObjectStoreDiagnostics> GetStorageDiagnosticsAsync(CancellationToken ct = default)
    {
        var result = new FileObjectStoreDiagnostics
        {
            RootExists = Directory.Exists(_rootPath)
        };
        if (!result.RootExists)
        {
            return Task.FromResult(result);
        }

        result.ContainerCount = Directory.EnumerateDirectories(_rootPath)
            .Count(path => !IsTempPath(path));
        foreach (var path in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (IsTempPath(path))
            {
                result.TemporaryFileCount++;
                result.TemporaryBytes += new FileInfo(path).Length;
                continue;
            }

            if (IsMetadataSidecar(path))
            {
                result.MetadataSidecarCount++;
                if (!File.Exists(path[..^MetadataSuffix.Length]))
                {
                    result.OrphanMetadataCount++;
                }

                continue;
            }

            result.ObjectCount++;
            result.ContentBytes += new FileInfo(path).Length;
            if (!File.Exists(GetMetadataPath(path)))
            {
                result.MissingMetadataCount++;
            }
        }

        result.Healthy = result.RootExists &&
            result.MissingMetadataCount == 0 &&
            result.OrphanMetadataCount == 0 &&
            result.TemporaryFileCount == 0;
        return Task.FromResult(result);
    }

    private static void ValidateWriteRequest(ObjectWriteRequest request)
    {
        ValidateContainer(request.Container);
        _ = NormalizeObjectKey(request.Key);
        ObjectMetadataValidator.ValidateUserMetadata(request.Metadata);
        if (request.Content == null) throw new InvalidOperationException("Object content stream is required.");
    }

    private static void ValidateWritePreconditions(ObjectWriteRequest request, ObjectInfo? existing)
    {
        if (!string.IsNullOrWhiteSpace(request.IfMatch) && !EtagMatches(request.IfMatch, existing?.Etag))
        {
            throw new InvalidOperationException("Object write precondition failed: ifMatch did not match the current etag.");
        }

        if (!string.IsNullOrWhiteSpace(request.IfNoneMatch))
        {
            if (request.IfNoneMatch == "*" && existing != null)
            {
                throw new InvalidOperationException("Object write precondition failed: ifNoneMatch '*' found an existing object.");
            }

            if (existing != null && EtagMatches(request.IfNoneMatch, existing.Etag))
            {
                throw new InvalidOperationException("Object write precondition failed: ifNoneMatch matched the current etag.");
            }
        }
    }

    private static int ValidateListLimit(int? limit)
    {
        if (limit <= 0)
        {
            throw new InvalidOperationException("Object list limit must be greater than zero.");
        }

        var effectiveLimit = limit ?? DefaultObjectListLimit;
        if (effectiveLimit > MaxObjectListLimit)
        {
            throw new InvalidOperationException($"Object list limit cannot exceed {MaxObjectListLimit}.");
        }

        return effectiveLimit;
    }

    private async Task<ObjectInfo?> ReadInfoAsync(string container, string key, CancellationToken ct)
    {
        var metadataPath = GetMetadataPath(ResolvePath(container, key));
        if (!File.Exists(metadataPath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, ct);
            return JsonSerializer.Deserialize<ObjectInfo>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ObjectInfo?> ResolveExistingInfoAsync(string container, string key, CancellationToken ct)
    {
        var info = await ReadInfoAsync(container, key, ct);
        if (info != null)
        {
            return info;
        }

        var fullPath = ResolvePath(container, key);
        return File.Exists(fullPath)
            ? await BuildContentOnlyInfoAsync(container, key, fullPath, ct)
            : null;
    }

    private async Task<ObjectInfo> BuildContentOnlyInfoAsync(string container, string key, string fullPath, CancellationToken ct)
    {
        var contentHash = await ComputeSha256Async(fullPath, ct);
        return new ObjectInfo
        {
            Container = container,
            Key = NormalizeObjectKey(key),
            ContentLength = new FileInfo(fullPath).Length,
            ContentHash = contentHash,
            Etag = contentHash,
            Metadata = new Dictionary<string, string>(),
            UpdatedAt = File.GetLastWriteTimeUtc(fullPath)
        };
    }

    private async Task WriteInfoAsync(ObjectInfo info, CancellationToken ct)
    {
        var metadataPath = GetMetadataPath(ResolvePath(info.Container, info.Key));
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        var tempPath = CreateTempPath();
        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(info), ct);
            File.Move(tempPath, metadataPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private string ResolvePath(string container, string key)
    {
        var containerPath = ResolveContainerPath(container);
        var normalizedKey = NormalizeObjectKey(key);
        var fullPath = Path.GetFullPath(Path.Combine(containerPath, normalizedKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(fullPath, containerPath))
        {
            throw new InvalidOperationException("Object key must remain inside the configured root.");
        }

        return fullPath;
    }

    private string ResolveContainerPath(string container)
    {
        ValidateContainer(container);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, container));
        if (!IsInside(fullPath, _rootPath))
        {
            throw new InvalidOperationException("Container path must remain inside the configured root.");
        }

        return fullPath;
    }

    private static string NormalizeObjectKey(string key, bool allowTrailingSlash = false)
    {
        return ObjectNameValidator.NormalizeObjectKey(key, allowTrailingSlash);
    }

    private static void ValidateContainer(string container)
    {
        ObjectNameValidator.ValidateContainer(container);
    }

    private static string GetMetadataPath(string fullPath) => fullPath + MetadataSuffix;

    private bool IsMetadataSidecar(string path)
    {
        if (!path.EndsWith(MetadataSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var contentPath = path[..^MetadataSuffix.Length];
        if (File.Exists(contentPath))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var info = JsonSerializer.Deserialize<ObjectInfo>(stream);
            if (info is null)
            {
                return false;
            }

            var expectedPath = GetMetadataPath(ResolvePath(info.Container, info.Key));
            return string.Equals(path, expectedPath, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private string CreateTempPath()
    {
        var tempDirectory = Path.Combine(_rootPath, TempDirectoryName);
        Directory.CreateDirectory(tempDirectory);
        return Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.tmp");
    }

    private async Task<ObjectFileLock> AcquireObjectLockAsync(string fullPath, CancellationToken ct)
    {
        var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath))).ToLowerInvariant() + ".lock";
        var lockPath = Path.Combine(_lockRootPath, lockName);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new ObjectFileLock(new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous));
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            }
        }
    }

    private bool IsTempPath(string path)
    {
        var tempDirectory = Path.GetFullPath(Path.Combine(_rootPath, TempDirectoryName));
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(tempDirectory, StringComparison.Ordinal) ||
            fullPath.StartsWith(tempDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temp cleanup is best effort; readiness diagnostics report stale temp files.
        }
    }

    private static bool EtagMatches(string requested, string? current)
    {
        if (requested == "*") return current != null;
        return string.Equals(requested, current, StringComparison.Ordinal);
    }

    private static bool IsInside(string path, string root)
    {
        return path.Equals(root, StringComparison.Ordinal) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static async Task<string> ComputeSha256Async(string fullPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(fullPath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string EncodeContinuationToken(int offset)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static int DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        return int.Parse(decoded, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ObjectFileLock(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
