using Stomme.FeatureManagement.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// Drives the config-backed feature source end-to-end against a live relational store, through the REAL
/// configuration providers a host uses — a <c>features.json</c> on disk, environment variables, the
/// Microsoft.FeatureManagement object schema — not a fake. Each test composes the module exactly as a host
/// does (<c>AddFeatureManagement</c> over SQLite + an <see cref="IConfiguration"/> built from real providers),
/// runs the real seeder, and OBSERVES the config value taking (or not taking) effect at evaluation time.
/// <para>
/// The precedence proven here is <b>config → DB → catalog default</b>: a config value overrides the admin
/// toggle and the catalog default; an absent config value falls through to the DB flag; config sets the
/// enabled switch but does NOT bypass licence entitlement.
/// </para>
/// </summary>
public sealed class FeatureConfigurationPrecedenceTests
{
    private const string EntitledFlag = "tests.config.entitled";

    private static ServiceProvider Compose(
        IConfiguration configuration,
        string databasePath,
        IEntitlementProvider? entitlement = null,
        IFeatureContributor? extraContributor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(configuration);

        if (entitlement is not null)
        {
            services.AddSingleton(entitlement);
        }

        if (extraContributor is not null)
        {
            // An instance registration coexists with the reflection-discovered contributors (TryAddEnumerable
            // dedups by implementation type), so this adds a flag WITHOUT touching the shared test contributor.
            services.AddSingleton(extraContributor);
        }

        services.AddFeatureManagement(options => options.UseSqlite($"Data Source={databasePath}"));
        return services.BuildServiceProvider();
    }

    private static async Task RunSeederAsync(ServiceProvider services)
    {
        foreach (var hosted in services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> IsEnabledAsync(ServiceProvider services, string key)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(key);
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"stomme-features-cfg-{Guid.NewGuid():N}.db");

    private static void DropDb(string path)
    {
        // THIS database's pool, by the exact string Compose handed to UseSqlite. ClearAllPools is process-wide
        // and drops the pooled connections of whatever else is running, mid-request. The one case that
        // composes with a DIFFERENT string (";Pooling=True") releases its own pool where it builds it — the pool
        // is keyed by the raw string, so this spelling would not reach it.
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

    /// <summary>A real features.json on disk overrides a flag the seeder set on — and leaves untouched a
    /// flag it does not mention (config → DB → default, in both directions, from an actual file).</summary>
    [Fact]
    public async Task Features_json_file_overrides_the_seeded_flag_and_leaves_others_to_the_db()
    {
        var dbPath = TempDb();
        var jsonPath = Path.Combine(Path.GetTempPath(), $"features-{Guid.NewGuid():N}.json");
        // notifications.email seeds ON (DefaultEnabled: true); the file forces it OFF. visitors.tracking is not
        // mentioned, so its seeded-on DB state must stand.
        await File.WriteAllTextAsync(jsonPath, """
            { "FeatureManagement": { "notifications.email": false } }
            """);

        var configuration = new ConfigurationBuilder().AddJsonFile(jsonPath, optional: false).Build();
        var services = Compose(configuration, dbPath);
        try
        {
            await RunSeederAsync(services);

            Assert.False(await IsEnabledAsync(services, FeatureCatalog.EmailNotifications)); // config wins
            Assert.True(await IsEnabledAsync(services, FeatureCatalog.VisitorTracking));     // DB stands
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(dbPath);
            File.Delete(jsonPath);
        }
    }

    /// <summary>An environment variable turns on a config-only module flag that has no DB row — the M5 knob,
    /// through the real environment-variable provider.</summary>
    [Fact]
    public async Task Environment_variable_enables_a_config_only_module_flag()
    {
        var dbPath = TempDb();
        const string moduleFlag = "module.blogs.enabled";
        // ASP.NET's double-underscore maps to the ':' config separator; the key keeps its dots verbatim.
        var envVar = "FeatureManagement__module.blogs.enabled";
        Environment.SetEnvironmentVariable(envVar, "true");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var services = Compose(configuration, dbPath);
            try
            {
                await RunSeederAsync(services);

                Assert.True(await IsEnabledAsync(services, moduleFlag)); // config-only enable, no DB row
                // A control flag with neither a DB row nor a config entry stays off.
                Assert.False(await IsEnabledAsync(services, "module.absent.enabled"));
            }
            finally
            {
                await services.DisposeAsync();
                DropDb(dbPath);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    /// <summary>The Microsoft.FeatureManagement object form's explicit <c>enabled</c> bit is honoured, so an
    /// Azure App Configuration definition (schema v2) that resolves to a definite state is a drop-in.</summary>
    [Fact]
    public async Task Microsoft_object_form_enabled_bit_overrides()
    {
        var dbPath = TempDb();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureManagement:notifications.email:enabled"] = "false",
            })
            .Build();
        var services = Compose(configuration, dbPath);
        try
        {
            await RunSeederAsync(services);
            Assert.False(await IsEnabledAsync(services, FeatureCatalog.EmailNotifications));
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(dbPath);
        }
    }

    /// <summary>Config sets the enabled switch but does NOT bypass licence entitlement: a config-true flag
    /// whose required entitlement is denied stays off; permit it and config's on takes effect.</summary>
    [Fact]
    public async Task Config_true_does_not_bypass_a_denied_entitlement()
    {
        var contributor = new FeatureContributor(
        [
            new FeatureDescriptor(EntitledFlag, "A flag gated on a licence entitlement.",
                Category: "Tests", DefaultEnabled: true, RequiredEntitlement: "premium"),
        ]);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"FeatureManagement:{EntitledFlag}"] = "true" })
            .Build();

        // Denied: config true cannot override the missing licence.
        var deniedDb = TempDb();
        var denied = Compose(configuration, deniedDb, new DenyingEntitlementProvider(), contributor);
        try
        {
            await RunSeederAsync(denied);
            Assert.False(await IsEnabledAsync(denied, EntitledFlag));
        }
        finally
        {
            await denied.DisposeAsync();
            DropDb(deniedDb);
        }

        // Granted: same config, same flag, now the entitlement is licensed, so config's on takes effect.
        var grantedDb = TempDb();
        var granted = Compose(configuration, grantedDb, new GrantingEntitlementProvider(), contributor);
        try
        {
            await RunSeederAsync(granted);
            Assert.True(await IsEnabledAsync(granted, EntitledFlag));
        }
        finally
        {
            await granted.DisposeAsync();
            DropDb(grantedDb);
        }
    }

    /// <summary>A config-true key with NO backing row is still gated when a
    /// registered descriptor requires an entitlement — the row-less branch no longer turns the flag on unconditionally.
    /// Driven through a real features.json on disk; the schema is created without seeding so the key stays row-less.</summary>
    [Fact]
    public async Task Config_true_row_less_key_with_an_entitled_descriptor_is_gated()
    {
        const string rowLessFlag = "tests.config.rowless-entitled";
        var contributor = new FeatureContributor(
        [
            new FeatureDescriptor(rowLessFlag, "A declared, not-yet-seeded flag gated on a licence entitlement.",
                Category: "Tests", DefaultEnabled: true, RequiredEntitlement: "novagloben.tests.premium"),
        ]);

        var jsonPath = Path.Combine(Path.GetTempPath(), $"features-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(jsonPath, $$"""
            { "FeatureManagement": { "{{rowLessFlag}}": true } }
            """);
        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(jsonPath, optional: false).Build();

            async Task<bool> EvaluateAsync(IEntitlementProvider provider)
            {
                var dbPath = TempDb();
                var connectionString = $"Data Source={dbPath};Pooling=True";
                var services = new ServiceCollection();
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton(configuration);
                services.AddSingleton(provider);
                services.AddSingleton<IFeatureContributor>(contributor);
                services.AddFeatureManagement(options => options.UseSqlite(connectionString));
                var provided = services.BuildServiceProvider();
                try
                {
                    await using (var scope = provided.CreateAsyncScope())
                    {
                        await scope.ServiceProvider.GetRequiredService<Infrastructure.FeatureManagementDbContext>()
                            .Database.EnsureCreatedAsync();
                    }

                    return await IsEnabledAsync(provided, rowLessFlag);
                }
                finally
                {
                    await provided.DisposeAsync();
                    // Clear only this database's pool.
                    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                    {
                        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
                    }

                    try
                    {
                        File.Delete(dbPath);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            Assert.False(await EvaluateAsync(new DenyingEntitlementProvider()));
            Assert.True(await EvaluateAsync(new GrantingEntitlementProvider()));
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }
    /// <summary>With no configuration source registered, evaluation is byte-for-byte the historical DB
    /// behaviour — the no-op source guarantees a stock install is unaffected.</summary>
    [Fact]
    public async Task No_configuration_leaves_the_db_state_authoritative()
    {
        var dbPath = TempDb();
        // Empty configuration: no FeatureManagement section at all.
        var configuration = new ConfigurationBuilder().Build();
        var services = Compose(configuration, dbPath);
        try
        {
            await RunSeederAsync(services);
            Assert.True(await IsEnabledAsync(services, FeatureCatalog.EmailNotifications));  // seeded on
            Assert.False(await IsEnabledAsync(services, "module.blogs.enabled"));            // unknown -> off
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(dbPath);
        }
    }

    /// <summary>
    /// Reproduces the Stomme host's config wiring: the WebApplicationBuilder's IConfiguration (no
    /// features.json) and Stomme's passed-in IConfigurationRoot (which loads features.json) are different
    /// objects in DI. The source must read the ROOT, so the dedicated feature file actually takes effect.
    /// </summary>
    [Fact]
    public async Task Host_reads_the_configuration_root_so_features_json_wins_over_builder_configuration()
    {
        var dbPath = TempDb();

        // Stands in for builder.Configuration: has an appsettings-like FeatureManagement entry turning the
        // flag ON — but this is NOT the object the source should trust in the host.
        var builderConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureManagement:notifications.email"] = "true",
            })
            .Build();

        // Stands in for Stomme's IConfigurationRoot (the one that loaded features.json), turning it OFF.
        var stommeRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureManagement:notifications.email"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(builderConfiguration);
        services.AddSingleton(stommeRoot); // registered as IConfigurationRoot, as the host does
        services.AddFeatureManagement(options => options.UseSqlite($"Data Source={dbPath}"));
        var provider = services.BuildServiceProvider();
        try
        {
            await RunSeederAsync(provider);
            // The root (features.json) says false and must win over builder.Configuration's true.
            Assert.False(await IsEnabledAsync(provider, FeatureCatalog.EmailNotifications));
        }
        finally
        {
            await provider.DisposeAsync();
            DropDb(dbPath);
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
