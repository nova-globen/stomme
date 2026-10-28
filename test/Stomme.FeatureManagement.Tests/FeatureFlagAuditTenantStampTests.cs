using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// Pins the audit trail's tenant stamp (BL-100 FM1, ported from the predecessor platform's
/// <c>TogglingAFlag_UnderATenantScope_StampsThatTenantOnTheAuditEntry</c>): a flag toggled under a tenant scope
/// writes its <see cref="FeatureFlagAudit"/> row stamped with THAT tenant, and the row is visible only under
/// that tenant - never under another tenant, nor the default one.
/// </summary>
/// <remarks>
/// The scope is entered through <see cref="ITenantScopeFactory.BeginScope"/>, the API the seeder uses
/// (<c>FeatureSeeder.SeedTenantAsync</c>), BEFORE the DI scope is created - the same ordering a background job
/// or the seeder follows. The host's real implementation (<c>TenantScopeFactory</c> + <c>StommeTenantContext</c>)
/// lives in <c>Stomme.Server</c>, which this module test project does not reference; the ambient double below
/// has its semantics (AsyncLocal, nests, restores on dispose) and is registered before the module so its
/// <c>DefaultTenantContext</c> fallback stands down.
/// </remarks>
public sealed class FeatureFlagAuditTenantStampTests
{
    private const string Key = "tests.audit.tenant-stamp";

    [Fact]
    public async Task A_toggle_under_a_tenant_scope_stamps_that_tenant_on_the_audit_row()
    {
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var databasePath = TempDb();
        var services = Compose(databasePath);
        try
        {
            await RunSeederAsync(services);
            var scopes = services.GetRequiredService<ITenantScopeFactory>();

            using (scopes.BeginScope(tenantA.Value))
            {
                await InScopeAsync(services, sp => sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(Key, true));

                var audits = await ReadAuditsAsync(services);
                var entry = Assert.Single(audits);
                Assert.Equal(tenantA.Value, entry.TenantId.Value);
                Assert.True(entry.Enabled);

                // The admin read path agrees with the store read.
                var views = await InScopeAsync(services, sp => sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(Key));
                Assert.Single(views);
            }

            using (scopes.BeginScope(tenantB.Value))
            {
                Assert.Empty(await ReadAuditsAsync(services));
                Assert.Empty(await InScopeAsync(services, sp => sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(Key)));
            }

            // Outside any scope the ambient falls back to the default tenant, which must not see A's row either.
            Assert.Empty(await ReadAuditsAsync(services));
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(databasePath);
        }
    }

    // ---- helpers ----

    private static ServiceProvider Compose(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        var ambient = new AmbientTenantScopeDouble();
        services.AddSingleton<ITenantContext>(ambient);
        services.AddSingleton<ITenantScopeFactory>(ambient);

        services.AddFeatureManagement(options => options.UseSqlite(ConnectionString(databasePath)));
        return services.BuildServiceProvider();
    }

    private static Task<IReadOnlyList<FeatureFlagAudit>> ReadAuditsAsync(ServiceProvider services) =>
        InScopeAsync(services, async sp =>
            (IReadOnlyList<FeatureFlagAudit>)(await sp.GetRequiredService<IRepository<FeatureFlagAudit, Guid>>().GetAllAsync())
                .Where(audit => audit.Key == Key)
                .ToList());

    private static async Task<T> InScopeAsync<T>(ServiceProvider services, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task RunSeederAsync(ServiceProvider services)
    {
        foreach (var hosted in services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"stomme-features-audit-{Guid.NewGuid():N}.db");

    private static string ConnectionString(string databasePath) => $"Data Source={databasePath};Pooling=True";

    private static void DropDb(string databasePath)
    {
        // Targeted: only this test's pool lets go of its file (BL-104 - never ClearAllPools).
        using (var connection = new SqliteConnection(ConnectionString(databasePath)))
        {
            SqliteConnection.ClearPool(connection);
        }

        try
        {
            File.Delete(databasePath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// An AsyncLocal ambient tenant: the tenant a scope entered, or the default tenant outside any scope (the
    /// module's own fallback). Never system scope - the store's tenant query filter must apply.
    /// </summary>
    private sealed class AmbientTenantScopeDouble : ITenantContext, ITenantScopeFactory
    {
        private readonly AsyncLocal<Guid?> _current = new();

        public TenantId? CurrentTenantId => _current.Value is { } id ? TenantId.From(id) : TenantId.Default;

        public bool IsSystemScope => false;

        public IDisposable BeginScope(Guid? tenantId)
        {
            var previous = _current.Value;
            _current.Value = tenantId;
            return new Restore(this, previous);
        }

        private sealed class Restore(AmbientTenantScopeDouble owner, Guid? previous) : IDisposable
        {
            public void Dispose() => owner._current.Value = previous;
        }
    }
}