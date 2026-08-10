using System.Security.Cryptography;

namespace Vyral.Providers.Cli;

/// <summary>
/// A bounded, content-addressed identity for a configured CLI executable.
/// It deliberately follows a symlink so replacing its target invalidates
/// provider qualification even when the command name is unchanged.
/// </summary>
internal sealed record CliExecutableIdentity(
    string Command,
    string? ResolvedPath,
    long? Length,
    string? Sha256,
    string? Error)
{
    private const long MaximumIdentityBytes = 256L * 1024 * 1024;

    public bool Available => ResolvedPath is not null && Length is not null && Sha256 is not null;

    public string ConfigMaterial => Available
        ? $"{ResolvedPath}|{Length}|{Sha256}"
        : $"unavailable:{Error}";

    public static CliExecutableIdentity Capture(string command)
    {
        var resolvedPath = ResolveCommandPath(command);
        if (resolvedPath is null)
        {
            return new CliExecutableIdentity(command, null, null, null, "command_not_found");
        }

        try
        {
            var target = File.ResolveLinkTarget(resolvedPath, returnFinalTarget: true)?.FullName ?? resolvedPath;
            var fullPath = Path.GetFullPath(target);
            var info = new FileInfo(fullPath);
            if (info.Length > MaximumIdentityBytes)
            {
                return new CliExecutableIdentity(command, fullPath, info.Length, null, "executable_too_large");
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new CliExecutableIdentity(command, fullPath, info.Length, hash, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CliExecutableIdentity(command, resolvedPath, null, null, "identity_unavailable");
        }
    }

    public static string? ResolveCommandPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
