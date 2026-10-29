#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.EventBus;
using Stomme.Mediator;
using Stomme.Modules.Pipelines.Startup;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// A named event raised for a site's pipelines (BL-95 increment (e), the predecessor platform's bus trigger re-homed onto the
    /// engine's own <c>Event</c> trigger): any module publishes one on the integration event bus, and the handler
    /// below turns it into the engine's event stimulus on that site - starting every published pipeline whose
    /// <c>Event</c> trigger names it and resuming every run waiting on it (<c>WaitForEvent</c>, the durable
    /// suspension). The payload rides as the event's input.
    /// </summary>
    public sealed record PipelineEventRaised(int TenantId, int SiteId, string Name, string? Payload, string? CorrelationId);

    /// <summary>
    /// The bus side. Discovered by the host's messaging scan into EVERY host, so it resolves the module's services
    /// lazily: with Pipelines off nothing is composed and the event is ignored - a registration with a constructor
    /// dependency the stock host lacks would fail its container validation instead.
    /// </summary>
    public sealed class PipelineEventRaisedHandler(IServiceProvider services, ILogger<PipelineEventRaisedHandler> logger) : IIntegrationEventHandler<PipelineEventRaised>
    {
        public Task HandleAsync(PipelineEventRaised @event, CancellationToken ct = default) => PipelineEventDelivery.DeliverAsync(services, @event, logger, ct);
    }

    /// <summary>The delivery itself: enter the site's engine tenant, then the engine's event publisher. Shared by the bus handler and the in-process path.</summary>
    internal static class PipelineEventDelivery
    {
        public static async Task DeliverAsync(IServiceProvider services, PipelineEventRaised @event, ILogger logger, CancellationToken ct)
        {
            if (services.GetService<ServerStartup.PipelinesComposed>() is null)
            {
                return;
            }

            var bridge = services.GetRequiredService<IEngineTenantBridge>();
            var tenant = await bridge.ResolveAsync(@event.TenantId, @event.SiteId, ct);
            using (bridge.Enter(tenant))
            {
                // asynchronous: the engine DISPATCHES the starts and resumes - a request that raises an event never runs a pipeline.
                await services.GetRequiredService<IEventPublisher>().PublishAsync(
                    @event.Name, @event.CorrelationId, payload: @event.Payload, asynchronous: true, cancellationToken: ct);
            }

            logger.LogInformation("Pipeline event '{Event}' delivered to site {Site} of tenant {Tenant}.", @event.Name, @event.SiteId, @event.TenantId);
        }
    }

    /// <summary>Raise an event for the current site's pipelines: what <c>POST api/Pipelines/events/{name}</c> does, per key.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Run)]
    public sealed record RaisePipelineEventCommand(string Name, string? Payload, string? CorrelationId) : ICommand<bool>;

    /// <summary>
    /// Publishes on the integration bus when the host composes one (so every node's engine sees it), and delivers
    /// in-process otherwise - the bus is optional in this host, and a handler that REQUIRED it would fail the container
    /// validation of every install without one (measured: five suites red on the scratch host).
    /// </summary>
    public sealed class RaisePipelineEventCommandHandler(IServiceProvider services, SiteState siteState, ILogger<RaisePipelineEventCommandHandler> logger) : ICommandHandler<RaisePipelineEventCommand, bool>
    {
        public async Task<bool> Handle(RaisePipelineEventCommand request, CancellationToken ct)
        {
            var alias = siteState.Alias;
            if (alias is null || string.IsNullOrWhiteSpace(request.Name))
            {
                return false;
            }

            var raised = new PipelineEventRaised(alias.TenantId, alias.SiteId, request.Name.Trim(), request.Payload, request.CorrelationId);
            if (services.GetService<IEventBus>() is { } bus)
            {
                await bus.PublishAsync(raised, ct);
            }
            else
            {
                await PipelineEventDelivery.DeliverAsync(services, raised, logger, ct);
            }

            return true;
        }
    }
}
