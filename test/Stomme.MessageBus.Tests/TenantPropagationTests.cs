using Stomme.EventBus;
using Stomme.Infrastructure.EventBus;
using Stomme.Persistence.Abstractions;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Stomme.MessageBus.Tests;

/// <summary>An event whose handler records which tenant scope it ran under.</summary>
public sealed record TenantPing(Guid Nonce) : IntegrationEvent;

/// <summary>Ambient tenant scope, AsyncLocal so it flows into the Rebus handler's async context.</summary>
public sealed class AmbientTenantScope : ITenantContext, ITenantScopeFactory
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public TenantId? CurrentTenantId => Current.Value is { } id ? TenantId.From(id) : null;

    public bool IsSystemScope => Current.Value is null;

    public IDisposable BeginScope(Guid? tenantId)
    {
        var previous = Current.Value;
        Current.Value = tenantId;
        return new Restore(() => Current.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>Captures the tenant the handler observed, not the one the publisher had.</summary>
public sealed class ObservedTenant
{
    private readonly TaskCompletionSource<Guid?> _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Guid?> Seen => _seen.Task;

    public void Record(Guid? tenantId) => _seen.TrySetResult(tenantId);
}

/// <summary>Reads the ambient tenant at handling time — it must be the publisher's tenant.</summary>
public sealed class TenantPingHandler(ITenantContext tenantContext, ObservedTenant observed)
    : IIntegrationEventHandler<TenantPing>
{
    public Task HandleAsync(TenantPing @event, CancellationToken ct = default)
    {
        observed.Record(tenantContext.CurrentTenantId?.Value);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Phase 4 — tenant propagation across the bus. The outgoing pipeline step stamps the ambient tenant on
/// the message; the incoming step restores that tenant's scope around dispatch, so a cross-context
/// handler (and everything it touches: repositories, audit writers) runs under the originating tenant
/// even though it executes on a bus worker thread with no request context.
/// </summary>
public class TenantPropagationTests
{
    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ObservedTenant>();

        // AddMessaging wires the tenant pipeline steps only when BOTH of these are registered.
        var scope = new AmbientTenantScope();
        builder.Services.AddSingleton<ITenantContext>(scope);
        builder.Services.AddSingleton<ITenantScopeFactory>(scope);

        builder.Services.AddMessaging(
            [typeof(TenantPropagationTests).Assembly],
            configure: options =>
            {
                options.InputQueue = "stomme-tenant-tests";
                options.ErrorQueue = "stomme-tenant-tests.error";
            });
        builder.Services.AddBusSubscription<TenantPing>();
        return builder.Build();
    }

    [Fact]
    public async Task Handler_runs_under_the_publishing_tenants_scope()
    {
        var tenantId = Guid.NewGuid();
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            var scopes = host.Services.GetRequiredService<ITenantScopeFactory>();
            using (var scope = host.Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                // Publish from inside tenant scope: the outgoing step stamps the header here.
                using (scopes.BeginScope(tenantId))
                {
                    await bus.PublishAsync(new TenantPing(Guid.NewGuid()), CancellationToken.None);
                }
            }

            var observed = host.Services.GetRequiredService<ObservedTenant>();
            var seen = await observed.Seen.WaitAsync(TimeSpan.FromSeconds(20));

            // The handler ran on a worker thread with no ambient tenant of its own — it saw the
            // publisher's tenant only because the header crossed the transport and was restored.
            Assert.Equal(tenantId, seen);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task System_scope_publishes_carry_no_tenant_and_handle_in_system_scope()
    {
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                // No tenant scope opened: a system-scope publish stamps no header.
                await bus.PublishAsync(new TenantPing(Guid.NewGuid()), CancellationToken.None);
            }

            var observed = host.Services.GetRequiredService<ObservedTenant>();
            var seen = await observed.Seen.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Null(seen);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
