using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Commands;
using Stomme.Engine.Pipelines.Runtime.Messages;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

// ReSharper disable once UnusedType.Global
[UsedImplicitly]
internal class DispatchPipelineCommandHandler(IStimulusSender stimulusSender, IPipelineRuntime pipelineRuntime) :
    ICommandHandler<DispatchTriggerPipelinesCommand>,
    ICommandHandler<DispatchPipelineDefinitionCommand>,
    ICommandHandler<DispatchPipelineInstanceCommand>,
    ICommandHandler<DispatchResumePipelinesCommand>
{
    public virtual async Task<Unit> HandleAsync(DispatchTriggerPipelinesCommand command, CancellationToken cancellationToken)
    {
        var stepTypeName = command.StepTypeName;
        var stimulus = command.Stimulus;
        var metadata = new StimulusMetadata
        {
            CorrelationId = command.CorrelationId,
            StepInstanceId = command.StepInstanceId,
            PipelineInstanceId = command.PipelineInstanceId,
            Input = command.Input,
            Properties = command.Properties,
        };
        await stimulusSender.SendAsync(stepTypeName, stimulus, metadata, cancellationToken).ConfigureAwait(false);

        return Unit.Instance;
    }

    public virtual async Task<Unit> HandleAsync(DispatchPipelineDefinitionCommand command, CancellationToken cancellationToken)
    {
        var client = await pipelineRuntime.CreateClientAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
        var createRequest = new CreateAndRunPipelineInstanceRequest
        {
            PipelineDefinitionHandle = PipelineDefinitionHandle.ByDefinitionVersionId(command.DefinitionVersionId),
            CorrelationId = command.CorrelationId,
            Input = command.Input,
            Properties = command.Properties,
            ParentId = command.ParentPipelineInstanceId,
            TriggerStepId = command.TriggerStepId,
            SchedulingStepExecutionId = command.SchedulingStepExecutionId,
            SchedulingPipelineInstanceId = command.SchedulingPipelineInstanceId,
            SchedulingCallStackDepth = command.SchedulingCallStackDepth
        };
        await client.CreateAndRunInstanceAsync(createRequest, cancellationToken).ConfigureAwait(false);
        return Unit.Instance;
    }

    public virtual async Task<Unit> HandleAsync(DispatchPipelineInstanceCommand command, CancellationToken cancellationToken)
    {
        var runRequest = new RunPipelineInstanceRequest
        {
            BookmarkId = command.BookmarkId,
            StepHandle = command.StepHandle,
            Input = command.Input,
            Properties = command.Properties
        };
        var client = await pipelineRuntime.CreateClientAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
        await client.RunInstanceAsync(runRequest, cancellationToken).ConfigureAwait(false);

        return Unit.Instance;
    }

    public virtual async Task<Unit> HandleAsync(DispatchResumePipelinesCommand command, CancellationToken cancellationToken)
    {
        var stepTypeName = command.StepTypeName;
        var stimulus = command.Stimulus;
        var metadata = new StimulusMetadata
        {
            CorrelationId = command.CorrelationId,
            PipelineInstanceId = command.PipelineInstanceId,
            StepInstanceId = command.StepInstanceId,
            Properties = command.Properties,
            Input = command.Input
        };
        await stimulusSender.SendAsync(stepTypeName, stimulus, metadata, cancellationToken).ConfigureAwait(false);

        return Unit.Instance;
    }
}