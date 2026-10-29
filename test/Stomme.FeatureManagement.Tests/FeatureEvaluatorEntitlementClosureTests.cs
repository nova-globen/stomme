using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.FeatureManagement.Infrastructure;
using Stomme.SharedKernel.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// D105 "one gate, closed properly" (the evaluator half; §J.BL-2 refutation 6): when the registry holds a
/// <see cref="FeatureDescriptor"/> for a key, the descriptor's <c>RequiredEntitlement</c> is the ONLY one the
/// evaluator consults — on the row path AND on the row-less config path. The stored row value applies only when
/// no descriptor is registered. This closes two holes measured at 9f65890: D84's row-less config-true key ran no
/// entitlement gate at all, and an upgrade whose descriptor starts requiring an entitlement never reached the
/// existing row (<c>FeatureFlag.RequiredEntitlement</c> is <c>private set</c> with no mutator, and the seeder only
/// creates missing rows).
/// <para>
/// Each fact composes the module exactly as <see cref="FeatureConfigurationPrecedenceTests"/> does
/// (<c>AddFeatureManagement</c> over a temp SQLite file). An upgrade is modelled as two containers over the SAME
/// file, the first disposed before the second opens it.
/// </para>
/// </summary>
public sealed class FeatureEvaluatorEntitlementClosureTests
{
    private const string Key = "tests.closure.gated";
    private const string Entitlement = "novagloben.tests.premium";

    private static ServiceProvider Compose(
        string connectionString,
        IEntitlementProvider? entitlement,
        IFeatureContributor? contributor,
        IReadOnlyDictionary<string, string?>? config = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build());

        if (entitlement is not null)
        {
            services.AddSingleton(entitlement);
        }

        if (contributor is not null)
        {
            services.AddSingleton(contributor);
        }

        services.AddFeatureManagement(options => options.UseSqlite(connectionString));
        return services.BuildServiceProvider();
    }

    private static FeatureContributor Descriptor(string? requiredEntitlement) => new(
    [
        new FeatureDescriptor(Key, "A flag whose licence gate is declared in code.",
            Category: "Tests", DefaultEnabled: true, RequiredEntitlement: requiredEntitlement),
    ]);

    private static Dictionary<string, string?> ConfigTrue() => new() { [$"FeatureManagement:{Key}"] = "true" };

    private static async Task RunSeederAsync(ServiceProvider services)
    {
        foreach (var hosted in services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    /// <summary>Creates the schema WITHOUT seeding, so a descriptor can be registered for a key that has no row.</summary>
    private static async Task EnsureSchemaWithoutSeedingAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FeatureManagementDbContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<FeatureFlag?> FindRowAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IRepository<FeatureFlag, Guid>>().GetAllAsync();
        return rows.FirstOrDefault(f => string.Equals(f.Key, Key, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> IsEnabledAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(Key);
    }

    private static string NewConnectionString() =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), $"stomme-features-closure-{Guid.NewGuid():N}.db"),
            Pooling = true,
        }.ToString();

    private static void Drop(string connectionString)
    {
        // Clear THIS database's pool only — ClearAllPools would yank connections from parallel tests.
        using (var connection = new SqliteConnection(connectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        try
        {
            File.Delete(new SqliteConnectionStringBuilder(connectionString).DataSource);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<bool> RowLessConfigTrueAsync(IEntitlementProvider provider)
    {
        var cs = NewConnectionString();
        var services = Compose(cs, provider, Descriptor(Entitlement), ConfigTrue());
        try
        {
            await EnsureSchemaWithoutSeedingAsync(services);
            Assert.NotNull(services.GetRequiredService<IFeatureRegistry>().Find(Key)); // the descriptor IS registered
            Assert.Null(await FindRowAsync(services));                                 // and no row backs it
            return await IsEnabledAsync(services);
        }
        finally
        {
            await services.DisposeAsync();
            Drop(cs);
        }
    }

    /// <summary>Two containers over one file: container 1 seeds the row from <paramref name="seededEntitlement"/>;
    /// container 2 (the "upgraded" host) registers <paramref name="upgraded"/> and evaluates with a denying provider.</summary>
    private static async Task<bool> UpgradeAsync(string? seededEntitlement, IFeatureContributor? upgraded)
    {
        var cs = NewConnectionString();
        try
        {
            var first = Compose(cs, entitlement: null, Descriptor(seededEntitlement));
            try
            {
                await RunSeederAsync(first);
                var row = await FindRowAsync(first);
                Assert.NotNull(row);
                Assert.Equal(seededEntitlement, row.RequiredEntitlement);
            }
            finally
            {
                await first.DisposeAsync();
            }

            var second = Compose(cs, new DenyingEntitlementProvider(), upgraded);
            try
            {
                await RunSeederAsync(second);
                // The seeder does not rewrite an existing row: the stored value is still the old one.
                Assert.Equal(seededEntitlement, (await FindRowAsync(second))!.RequiredEntitlement);
                return await IsEnabledAsync(second);
            }
            finally
            {
                await second.DisposeAsync();
            }
        }
        finally
        {
            Drop(cs);
        }
    }

    [Fact]
    public async Task Row_less_config_true_key_whose_descriptor_requires_an_entitlement_is_denied_by_a_denying_provider()
    {
        Assert.False(await RowLessConfigTrueAsync(new DenyingEntitlementProvider()));
    }

    [Fact]
    public async Task Row_less_config_true_key_whose_descriptor_requires_an_entitlement_is_allowed_by_a_granting_provider()
    {
        Assert.True(await RowLessConfigTrueAsync(new GrantingEntitlementProvider()));
    }

    [Fact]
    public async Task Pre_existing_row_without_entitlement_plus_descriptor_now_declaring_one_is_denied()
    {
        Assert.False(await UpgradeAsync(seededEntitlement: null, upgraded: Descriptor(Entitlement)));
    }

    [Fact]
    public async Task Row_declaring_an_entitlement_plus_descriptor_that_dropped_it_is_allowed()
    {
        Assert.True(await UpgradeAsync(seededEntitlement: Entitlement, upgraded: Descriptor(null)));
    }

    [Fact]
    public async Task No_descriptor_falls_back_to_the_row_value()
    {
        // Container 2 registers no descriptor for the key (the module was uninstalled): the row's stale
        // entitlement still applies, so a denying provider denies — harmless, nothing consumes the key.
        Assert.False(await UpgradeAsync(seededEntitlement: Entitlement, upgraded: null));
    }

    [Fact]
    public async Task No_provider_registered_stays_permissive()
    {
        var cs = NewConnectionString();
        var services = Compose(cs, entitlement: null, Descriptor(Entitlement));
        try
        {
            await RunSeederAsync(services);
            Assert.Equal(Entitlement, (await FindRowAsync(services))!.RequiredEntitlement);
            Assert.True(await IsEnabledAsync(services));
        }
        finally
        {
            await services.DisposeAsync();
            Drop(cs);
        }
    }

    private sealed class DenyingEntitlementProvider : IEntitlementProvider
    {
        public Task<bool> IsEntitledAsync(string capability, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class GrantingEntitlementProvider : IEntitlementProvider
    {
        public Task<bool> IsEntitledAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }
}