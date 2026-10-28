using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Mediator;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Tenants.Mediator;
using Stomme.Engine.Pipelines.Runtime.Commands;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// A simple implementation that queues the specified request for delivering stimuli on a non-durable background worker.
/// </summary>
public class BackgroundStimulusDispatcher(ICommandSender commandSender, ITenantAccessor tenantAccessor) : IStimulusDispatcher
{
    /// <inheritdoc />
    public async Task<DispatchStimulusResponse> SendAsync(DispatchStimulusRequest request, CancellationToken cancellationToken = default)
    {
        var command = new DispatchStimulusCommand(request);
        
        // Background commands run independently of caller's lifecycle.
        await commandSender.SendAsync(command, CommandStrategy.Background, CreateHeaders(), CancellationToken.None).ConfigureAwait(false);
        return DispatchStimulusResponse.Empty;
    }

    private IDictionary<object, object> CreateHeaders()
    {
        return TenantHeaders.CreateHeaders(tenantAccessor.Tenant?.Id);
    }
}
