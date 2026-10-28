using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Mediator;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Tenants.Mediator;
using Stomme.Engine.Pipelines.Runtime.Commands;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// A simple implementation that queues the specified request for pipeline execution on a non-durable background worker.
/// </summary>
public class BackgroundPipelineDispatcher(ICommandSender commandSender, INotificationSender notificationSender, ITenantAccessor tenantAccessor) : IPipelineDispatcher
{
    /// <inheritdoc />
    public async Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineDefinitionRequest request, DispatchPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Emit dispatching notification
        await notificationSender.SendAsync(new PipelineDefinitionDispatching(request), cancellationToken).ConfigureAwait(false);
        
        var command = new DispatchPipelineDefinitionCommand(request.DefinitionVersionId)
        {
            Input = request.Input,
            Properties = request.Properties,
            CorrelationId = request.CorrelationId,
            InstanceId = request.InstanceId,
            TriggerStepId = request.TriggerStepId,
            ParentPipelineInstanceId = request.ParentPipelineInstanceId,
            SchedulingStepExecutionId = request.SchedulingStepExecutionId,
            SchedulingPipelineInstanceId = request.SchedulingPipelineInstanceId,
            SchedulingCallStackDepth = request.SchedulingCallStackDepth
        };
        
        // Background commands run independently of caller's lifecycle.
        await commandSender.SendAsync(command, CommandStrategy.Background, CreateHeaders(), CancellationToken.None).ConfigureAwait(false);
        var response = DispatchPipelineResponse.Success();
        
        // Emit dispatched notification
        await notificationSender.SendAsync(new PipelineDefinitionDispatched(request, response), cancellationToken).ConfigureAwait(false);
        
        return response;
    }

    /// <inheritdoc />
    public async Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineInstanceRequest request, DispatchPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Emit dispatching notification
        await notificationSender.SendAsync(new PipelineInstanceDispatching(request), cancellationToken).ConfigureAwait(false);
        
        var command = new DispatchPipelineInstanceCommand(request.InstanceId){
            BookmarkId = request.BookmarkId,
            StepHandle = request.StepHandle,
            Input = request.Input,
            Properties = request.Properties,
            CorrelationId = request.CorrelationId};

        // Background commands run independently of caller's lifecycle.
        await commandSender.SendAsync(command, CommandStrategy.Background, CreateHeaders(), CancellationToken.None).ConfigureAwait(false);
        var response = DispatchPipelineResponse.Success();
        
        // Emit dispatched notification
        await notificationSender.SendAsync(new PipelineInstanceDispatched(request, response), cancellationToken).ConfigureAwait(false);
        
        return response;
    }

    /// <inheritdoc />
    public async Task<DispatchPipelineResponse> DispatchAsync(DispatchTriggerPipelinesRequest request, DispatchPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        var command = new DispatchTriggerPipelinesCommand(request.StepTypeName, request.BookmarkPayload)
        {
            CorrelationId = request.CorrelationId,
            PipelineInstanceId = request.PipelineInstanceId,
            StepInstanceId = request.StepInstanceId,
            Input = request.Input,
            Properties = request.Properties
        };
        
        // Background commands run independently of caller's lifecycle.
        await commandSender.SendAsync(command, CommandStrategy.Background, CreateHeaders(), CancellationToken.None).ConfigureAwait(false);
        return DispatchPipelineResponse.Success();
    }

    /// <inheritdoc />
    public async Task<DispatchPipelineResponse> DispatchAsync(DispatchResumePipelinesRequest request, DispatchPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        var command = new DispatchResumePipelinesCommand(request.StepTypeName, request.BookmarkPayload)
        {
            CorrelationId = request.CorrelationId,
            PipelineInstanceId = request.PipelineInstanceId,
            StepInstanceId = request.StepInstanceId,
            Input = request.Input
        };
        
        // Background commands run independently of caller's lifecycle.
        await commandSender.SendAsync(command, CommandStrategy.Background, CreateHeaders(), CancellationToken.None).ConfigureAwait(false);
        return DispatchPipelineResponse.Success();
    }
    
    private IDictionary<object, object> CreateHeaders()
    {
        return TenantHeaders.CreateHeaders(tenantAccessor.Tenant?.Id);
    }
}