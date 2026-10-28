using Stomme.FeatureManagement.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// Stands the feature-flag module up exactly as a host does — AddFeatureManagement over a real relational
/// store — against a throwaway SQLite file, then runs the hosted services so the real seeder creates the
/// schema and materialises the declared catalog. Nothing is faked: the seam's EF repositories, the evaluator,
/// the domain-event dispatch, and the audit trail are the production types.
/// </summary>
public sealed class FeatureStoreFixture : IDisposable
{
    private readonly string _databasePath;
    private readonly ServiceProvider _services;

    public FeatureStoreFixture()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"stomme-features-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        // Registered before AddFeatureManagement so the module's anonymous fallback stands down. This is the
        // seam a host uses to supply its principal (Stomme's server registers an HTTP-backed provider).
        services.AddSingleton(Principal);
        services.AddSingleton<IFeatureContextProvider>(Principal);

        services.AddFeatureManagement(options => options.UseSqlite($"Data Source={_databasePath}"));

        _services = services.BuildServiceProvider();

        foreach (var hosted in _services.GetServices<IHostedService>())
        {
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary>The principal every evaluation in a test sees; mutate it to switch users/roles.</summary>
    public TestFeatureContextProvider Principal { get; } = new();

    /// <summary>Runs <paramref name="action"/> in its own DI scope, like a request would.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task<IReadOnlyList<FeatureView>> ListAsync() =>
        InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>().ListAsync());

    public Task<bool> IsEnabledAsync(string key) =>
        InScopeAsync(sp => sp.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(key));

    public void Dispose()
    {
        _services.Dispose();
        // The pooled SQLite connection must let go of the file before it can be removed — THIS database's pool,
        // by the exact string the fixture composed with. ClearAllPools is process-wide and drops the pooled
        // connections of whatever else happens to be running, mid-request (BL-104).
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a green test run over.
        }
    }
}

/// <summary>A mutable <see cref="IFeatureContextProvider"/> so a test can evaluate as any user/role set.</summary>
public sealed class TestFeatureContextProvider : IFeatureContextProvider
{
    private FeatureContext _context = FeatureContext.Anonymous;

    public void SetAnonymous() => _context = FeatureContext.Anonymous;

    public void SetUser(Guid userId, params string[] roles) =>
        _context = new FeatureContext(userId, new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase));

    public FeatureContext GetContext() => _context;
}
