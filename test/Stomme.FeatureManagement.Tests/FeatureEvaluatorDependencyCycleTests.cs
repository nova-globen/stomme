using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// Pins the evaluator's flag-dependency semantics: a flag with a <c>RequiredFlag</c> is on only while
/// that flag is on, and a dependency CYCLE (a requires b, b requires a) evaluates to OFF without throwing.
/// </summary>
/// <remarks>
/// <b>Why a pass-through, yielding cache.</b> The cycle guard (<c>if (!visited.Add(key)) return false;</c>) is
/// the only thing between a cycle and unbounded recursion. With the real cache warm, each recursion level
/// completes synchronously, so a missing guard is a STACK OVERFLOW - which kills the test host rather than
/// failing a test, and a mutation check that crashes the runner proves nothing. The test cache below yields and
/// honours the cancellation token on every load, so a missing guard becomes a bounded, cancellable loop that
/// fails THIS test by timeout. It changes nothing the guard depends on: the evaluator reads the same snapshots
/// from the same store, just never from a warm entry.
/// </remarks>
public sealed class FeatureEvaluatorDependencyCycleTests
{
    private static readonly TimeSpan EvaluationBound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_dependency_cycle_evaluates_to_off_and_does_not_throw()
    {
        var databasePath = TempDb();
        var services = Compose(databasePath);
        try
        {
            await RunSeederAsync(services);
            await AddFlagsAsync(services,
                FeatureFlag.Create(Guid.NewGuid(), TenantId.Default, "tests.cycle.a", true, requiredFlag: "tests.cycle.b"),
                FeatureFlag.Create(Guid.NewGuid(), TenantId.Default, "tests.cycle.b", true, requiredFlag: "tests.cycle.a"));

            Assert.False(await EvaluateBoundedAsync(services, "tests.cycle.a"));
            Assert.False(await EvaluateBoundedAsync(services, "tests.cycle.b"));
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(databasePath);
        }
    }

    [Fact]
    public async Task A_flag_follows_the_flag_it_requires_on_and_off()
    {
        var databasePath = TempDb();
        var services = Compose(databasePath);
        try
        {
            await RunSeederAsync(services);
            var @base = FeatureFlag.Create(Guid.NewGuid(), TenantId.Default, "tests.dep.base", true);
            var feature = FeatureFlag.Create(Guid.NewGuid(), TenantId.Default, "tests.dep.feature", true, requiredFlag: "tests.dep.base");
            await AddFlagsAsync(services, @base, feature);

            Assert.True(await EvaluateBoundedAsync(services, "tests.dep.feature"));

            await SetAsync(services, "tests.dep.base", false);
            Assert.False(await EvaluateBoundedAsync(services, "tests.dep.feature"));

            await SetAsync(services, "tests.dep.base", true);
            Assert.True(await EvaluateBoundedAsync(services, "tests.dep.feature"));
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

        // Registered before the module composes, so its FeatureFlagCache TryAdd stands down.
        services.AddSingleton<IFeatureFlagCache, YieldingPassThroughFeatureFlagCache>();

        services.AddFeatureManagement(options => options.UseSqlite(ConnectionString(databasePath)));
        return services.BuildServiceProvider();
    }

    private static async Task<bool> EvaluateBoundedAsync(ServiceProvider services, string key)
    {
        using var cancellation = new CancellationTokenSource(EvaluationBound);
        await using var scope = services.CreateAsyncScope();
        var evaluation = scope.ServiceProvider.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(key, cancellation.Token);

        // The backstop: even if cancellation were never observed, the test fails rather than hangs.
        var winner = await Task.WhenAny(evaluation, Task.Delay(EvaluationBound + EvaluationBound));
        Assert.True(ReferenceEquals(winner, evaluation), $"evaluating '{key}' did not complete within {EvaluationBound + EvaluationBound} - an unguarded dependency cycle.");
        return await evaluation;
    }

    private static async Task AddFlagsAsync(ServiceProvider services, params FeatureFlag[] flags)
    {
        await using var scope = services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<FeatureFlag, Guid>>();
        foreach (var flag in flags)
        {
            await repository.AddAsync(flag);
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private static async Task SetAsync(ServiceProvider services, string key, bool enabled)
    {
        await using var scope = services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<FeatureFlag, Guid>>();
        var flag = (await repository.GetAllAsync()).Single(f => f.Key == key);
        flag.Set(enabled);
        await repository.UpdateAsync(flag);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private static async Task RunSeederAsync(ServiceProvider services)
    {
        foreach (var hosted in services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"stomme-features-cycle-{Guid.NewGuid():N}.db");

    private static string ConnectionString(string databasePath) => $"Data Source={databasePath};Pooling=True";

    private static void DropDb(string databasePath)
    {
        // Targeted: only this test's pool lets go of its file (never ClearAllPools).
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

    /// <summary>Never serves a warm entry, always yields, and honours cancellation on every load.</summary>
    private sealed class YieldingPassThroughFeatureFlagCache : IFeatureFlagCache
    {
        public async Task<IReadOnlyList<FeatureFlagSnapshot>> GetOrLoadAsync(
            TenantId tenantId,
            Func<CancellationToken, Task<IReadOnlyList<FeatureFlagSnapshot>>> load,
            CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return await load(ct);
        }

        public void Invalidate(TenantId tenantId)
        {
        }
    }
}