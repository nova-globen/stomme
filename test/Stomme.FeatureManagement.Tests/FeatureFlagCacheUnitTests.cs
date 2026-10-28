using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Infrastructure;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// Direct pins on the <see cref="FeatureFlagCache"/> mechanics the end-to-end tests cannot isolate: the
/// write-race version stamping (data loaded BEFORE an invalidation must never be CACHED after it — the
/// stale-forever race the version stamps exist to close) and TTL expiry (an expired entry forces a fresh
/// load; it never serves).
/// </summary>
public sealed class FeatureFlagCacheUnitTests
{
    private static readonly IReadOnlyList<FeatureFlagSnapshot> StaleList = [Snapshot("stale.flag")];
    private static readonly IReadOnlyList<FeatureFlagSnapshot> FreshList = [Snapshot("fresh.flag")];

    private static FeatureFlagSnapshot Snapshot(string key) => new(
        key, Enabled: true, RequiredEntitlement: null, RequiredFlag: null,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), RolloutPercentage: 100,
        new Dictionary<Guid, bool>());

    /// <summary>
    /// The race: an evaluation's load is in flight when a write commits and invalidates. The load's caller
    /// gets what it read (its read genuinely began before the write) — but that pre-write data must NOT be
    /// cached, or it would be served until the next write. The version stamp closes exactly this.
    /// </summary>
    [Fact]
    public async Task A_load_raced_by_an_invalidation_is_returned_but_never_cached()
    {
        var cache = new FeatureFlagCache(TimeProvider.System, TimeSpan.FromMinutes(5));
        var tenant = TenantId.From(Guid.NewGuid());

        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = cache.GetOrLoadAsync(tenant, async _ =>
        {
            loadStarted.SetResult();
            await releaseLoad.Task;
            return StaleList;
        });

        await loadStarted.Task;
        cache.Invalidate(tenant); // the write commits while the read is in flight
        releaseLoad.SetResult();

        Assert.Same(StaleList, await inFlight); // the racing caller gets its own (pre-write) read...

        var next = await cache.GetOrLoadAsync(tenant, _ => Task.FromResult(FreshList));
        Assert.Same(FreshList, next); // ...but the next evaluation reloads — the stale list was never cached
    }

    [Fact]
    public async Task A_cached_entry_serves_without_reloading_until_invalidated()
    {
        var cache = new FeatureFlagCache(TimeProvider.System, TimeSpan.FromMinutes(5));
        var tenant = TenantId.From(Guid.NewGuid());
        var loads = 0;

        for (var i = 0; i < 3; i++)
        {
            await cache.GetOrLoadAsync(tenant, _ =>
            {
                loads++;
                return Task.FromResult(StaleList);
            });
        }

        Assert.Equal(1, loads);

        cache.Invalidate(tenant);
        var fresh = await cache.GetOrLoadAsync(tenant, _ => Task.FromResult(FreshList));
        Assert.Same(FreshList, fresh);
    }

    /// <summary>Tenant entries are independent: invalidating one tenant leaves the other's entry serving.</summary>
    [Fact]
    public async Task Invalidation_is_per_tenant()
    {
        var cache = new FeatureFlagCache(TimeProvider.System, TimeSpan.FromMinutes(5));
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());

        await cache.GetOrLoadAsync(tenantA, _ => Task.FromResult(StaleList));
        await cache.GetOrLoadAsync(tenantB, _ => Task.FromResult(StaleList));

        cache.Invalidate(tenantA);

        var reloadedA = await cache.GetOrLoadAsync(tenantA, _ => Task.FromResult(FreshList));
        var servedB = await cache.GetOrLoadAsync(tenantB, _ => Task.FromResult(FreshList));

        Assert.Same(FreshList, reloadedA); // A reloaded
        Assert.Same(StaleList, servedB);   // B still serves its cached entry
    }

    /// <summary>
    /// TTL: the bound on how long ANOTHER process's write can go unseen. An expired entry never serves —
    /// the next read reloads fresh.
    /// </summary>
    [Fact]
    public async Task An_expired_entry_reloads_instead_of_serving()
    {
        var time = new TestTime();
        var cache = new FeatureFlagCache(time, TimeSpan.FromSeconds(30));
        var tenant = TenantId.From(Guid.NewGuid());

        await cache.GetOrLoadAsync(tenant, _ => Task.FromResult(StaleList));

        time.Now += TimeSpan.FromSeconds(29);
        var withinTtl = await cache.GetOrLoadAsync(tenant, _ => Task.FromResult(FreshList));
        Assert.Same(StaleList, withinTtl); // still inside the TTL: served from cache

        time.Now += TimeSpan.FromSeconds(2);
        var expired = await cache.GetOrLoadAsync(tenant, _ => Task.FromResult(FreshList));
        Assert.Same(FreshList, expired);   // expired: reloaded
    }

    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
