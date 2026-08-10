using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public sealed class CanonicalStorePreflightTests
{
    [Fact]
    public async Task DataPlanePreflight_FailsClosedAndRedactsProviderDiagnostics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vyral-canonical-preflight-failure-{Guid.NewGuid():N}.sqlite");
        var store = ArchiveRestoreFailureProxy.Create(new SqliteCanonicalStore(path));

        var result = await store.RunDataPlanePreflightAsync();

        Assert.False(result.Ready);
        Assert.Equal(CanonicalPreflightCheckStatuses.Failed, result.Status);
        Assert.False(result.BackupRestoreVerified);
        Assert.False(result.TenantIsolationVerified);
        Assert.True(result.CleanupVerified);
        Assert.Equal(CanonicalPreflightCheckStatuses.Failed, result.Checks.Single(item => item.Id == "canonical.archive_restore").Status);
        Assert.Equal(CanonicalPreflightCheckStatuses.Passed, result.Checks.Single(item => item.Id == "canonical.probe_cleanup").Status);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("localhost", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("probe-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connection", json, StringComparison.OrdinalIgnoreCase);
    }

    private class ArchiveRestoreFailureProxy : DispatchProxy
    {
        private ICanonicalStore? _inner;

        public static ICanonicalStore Create(ICanonicalStore inner)
        {
            var proxy = Create<ICanonicalStore, ArchiveRestoreFailureProxy>();
            ((ArchiveRestoreFailureProxy)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || _inner is null) throw new InvalidOperationException("Canonical preflight test proxy is not initialized.");
            if (string.Equals(targetMethod.Name, nameof(ICanonicalStore.RestoreTenantArchiveAsync), StringComparison.Ordinal))
            {
                return Task.FromException(new InvalidOperationException("Server=localhost;Password=probe-secret;Connection=unsafe"));
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
