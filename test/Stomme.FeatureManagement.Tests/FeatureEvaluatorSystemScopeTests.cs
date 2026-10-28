using Stomme.FeatureManagement.Application;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// BL-110 (a): in SYSTEM scope - an explicit <c>BeginScope(null)</c>, the shape of a job that has not entered a tenant -
/// the evaluator used to load EVERY tenant's flag rows and take the first one with the key, so the answer was some
/// tenant's switch, whichever the store returned first. System scope now has a defined answer: there is no tenant, so no
/// tenant's row applies, and only installation-wide configuration decides.
/// </summary>
public sealed class FeatureEvaluatorSystemScopeTests
{
    private const string Key = "tests.system-scope";

    [Fact]
    public async Task System_scope_answers_from_NO_tenants_row_while_each_tenant_keeps_its_own()
    {
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var context = new ScopedTenantContext(tenantA);
        var path = Path.Combine(Path.GetTempPath(), $"stomme-features-system-scope-{Guid.NewGuid():N}.db");
        var services = Compose(path, context, new ConfigurationBuilder().Build());
        try
        {
            await StartAsync(services);

            // Both tenants turn the flag ON. Before the fix, system scope then answered TRUE - from whichever tenant's row
            // the store listed first - although no tenant was in scope at all.
            await ToggleAsync(services, true);
            context.Switch(tenantB);
            await ToggleAsync(services, true);

            context.EnterSystemScope();
            Assert.False(await IsEnabledAsync(services));

            // Each tenant still gets its OWN answer (and a tenant that turns it off is not overruled by the other).
            context.Switch(tenantA);
            Assert.True(await IsEnabledAsync(services));
            context.Switch(tenantB);
            await ToggleAsync(services, false);
            Assert.False(await IsEnabledAsync(services));
            context.Switch(tenantA);
            Assert.True(await IsEnabledAsync(services));
        }
        finally
        {
            await services.DisposeAsync();
            Drop(path);
        }
    }

    [Fact]
    public async Task In_system_scope_installation_wide_CONFIGURATION_still_decides()
    {
        var context = new ScopedTenantContext(TenantId.From(Guid.NewGuid()));
        var path = Path.Combine(Path.GetTempPath(), $"stomme-features-system-scope-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FeatureManagement:" + Key] = "true",
        }).Build();
        var services = Compose(path, context, configuration);
        try
        {
            await StartAsync(services);
            context.EnterSystemScope();
            Assert.True(await IsEnabledAsync(services));
        }
        finally
        {
            await services.DisposeAsync();
            Drop(path);
        }
    }

    private static ServiceProvider Compose(string path, ITenantContext tenantContext, IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(configuration);
        services.AddSingleton(tenantContext);   // before the module composes, so its DefaultTenantContext stands down
        services.AddFeatureManagement(options => options.UseSqlite($"Data Source={path}"));
        return services.BuildServiceProvider();
    }

    private static async Task StartAsync(ServiceProvider services)
    {
        foreach (var hosted in services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> IsEnabledAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(Key);
    }

    private static async Task ToggleAsync(ServiceProvider services, bool enabled)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IFeatureAdminService>().ToggleAsync(Key, enabled);
    }

    private static void Drop(string path)
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A tenant context that can be one tenant, another, or SYSTEM scope (no tenant, <see cref="IsSystemScope"/>).</summary>
    private sealed class ScopedTenantContext(TenantId initial) : ITenantContext
    {
        private TenantId? _current = initial;

        public TenantId? CurrentTenantId => _current;

        public bool IsSystemScope => _current is null;

        public void Switch(TenantId tenant) => _current = tenant;

        public void EnterSystemScope() => _current = null;
    }
}
