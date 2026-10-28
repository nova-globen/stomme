using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Mediator;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Tenants.Mediator;
using Stomme.Engine.Pipelines.Runtime.Commands;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
///     Dispatches pipeline cancellation requests to a local background worker.
/// </summary>
public class BackgroundPipelineCancellationDispatcher(ICommandSender commandSender, ITenantAccessor tenantAccessor) : IPipelineCancellationDispatcher
{
    /// <inheritdoc />
    public async Task<DispatchCancelPipelinesResponse> DispatchAsync(DispatchCancelPipelineRequest request, CancellationToken cancellationToken = default)
    {
        var command = new CancelPipelinesCommand(request);
        
        // Background commands run independently of caller's lifecycle.
        await commandSender.SendAsync(command, CommandStrategy.Background, CreateHeaders(), CancellationToken.None).ConfigureAwait(false);
        return new();
    }

    private IDictionary<object, object> CreateHeaders()
    {
        return TenantHeaders.CreateHeaders(tenantAccessor.Tenant?.Id);
    }
}