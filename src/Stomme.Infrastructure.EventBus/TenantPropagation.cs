using Stomme.Persistence.Abstractions;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Rebus.Messages;
using Rebus.Pipeline;

namespace Stomme.Infrastructure.EventBus;

/// <summary>The transport header carrying the originating tenant id (a GUID); absent means system scope.</summary>
public static class TenantPropagationHeaders
{
    public const string TenantId = "stomme-tenant";
}

/// <summary>
/// Outgoing pipeline step that stamps the ambient tenant onto a message before it is serialized, so the
/// receiving endpoint can restore the same tenant scope. System-scope sends carry no header.
///
/// <see cref="ITenantContext"/> is a SCOPED service, so it is resolved per message from a scope the step
/// opens itself — never from the root provider at bus-configuration time, which throws. Reading it from a
/// child scope is correct because the port models the <em>ambient</em> tenant (an implementation is backed by
/// AsyncLocal or the ambient HttpContext), so every scope in the same async flow observes the same tenant.
/// </summary>
public sealed class TenantPropagationOutgoingStep(IServiceScopeFactory scopeFactory) : IOutgoingStep
{
    public async Task Process(OutgoingStepContext context, Func<Task> next)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var tenantId = scope.ServiceProvider.GetService<ITenantContext>()?.CurrentTenantId;
            if (tenantId is not null)
            {
                var message = context.Load<Message>();
                message.Headers[TenantPropagationHeaders.TenantId] = tenantId.Value.ToString();
            }
        }

        await next();
    }
}

/// <summary>
/// Incoming pipeline step that restores the originating tenant scope around message handling: it reads the
/// tenant header and opens a tenant (or system, when absent) scope for the duration of the dispatch, so
/// handlers — and anything they touch (repositories, audit writers) — run under the right tenant.
///
/// <see cref="ITenantScopeFactory"/> is resolved per message from a scope the step opens itself, for the same
/// reason as <see cref="TenantPropagationOutgoingStep"/>: the bus is a singleton built from the root provider,
/// and a host is free to register the port scoped.
/// </summary>
public sealed class TenantScopeIncomingStep(IServiceScopeFactory scopeFactory) : IIncomingStep
{
    public async Task Process(IncomingStepContext context, Func<Task> next)
    {
        var message = context.Load<Message>();
        Guid? tenantId = message.Headers.TryGetValue(TenantPropagationHeaders.TenantId, out var raw)
            && Guid.TryParse(raw, out var parsed)
                ? parsed
                : null;

        using var scope = scopeFactory.CreateScope();
        var tenantScopes = scope.ServiceProvider.GetService<ITenantScopeFactory>();
        if (tenantScopes is null)
        {
            await next();
            return;
        }

        // The ambient tenant scope must stay open for the whole dispatch, so it wraps next() and is
        // disposed before the DI scope that produced the factory.
        using (tenantScopes.BeginScope(tenantId))
        {
            await next();
        }
    }
}
