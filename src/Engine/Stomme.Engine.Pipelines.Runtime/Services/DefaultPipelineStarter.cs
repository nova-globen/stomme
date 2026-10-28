using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Exceptions;
using Stomme.Engine.Pipelines.Runtime.Messages;

namespace Stomme.Engine.Pipelines.Runtime;

public class DefaultPipelineStarter(IPipelineDefinitionService pipelineDefinitionService, IPipelineActivationStrategyEvaluator pipelineActivationStrategyEvaluator, IPipelineRuntime pipelineRuntime) : IPipelineStarter
{
    public async Task<StartPipelineResponse> StartPipelineAsync(StartPipelineRequest request, CancellationToken cancellationToken = default)
    {
        var pipeline = await GetPipelineAsync(request, cancellationToken).ConfigureAwait(false);

        var canStart = await pipelineActivationStrategyEvaluator.CanStartPipelineAsync(new()
        {
            Pipeline = pipeline,
            CorrelationId = request.CorrelationId
        }).ConfigureAwait(false);

        if (!canStart)
            return new()
            {
                CannotStart = true
            };

        var pipelineClient = await pipelineRuntime.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var createPipelineInstanceRequest = new CreateAndRunPipelineInstanceRequest
        {
            PipelineDefinitionHandle = PipelineDefinitionHandle.ByDefinitionVersionId(pipeline.Identity.Id),
            CorrelationId = request.CorrelationId,
            Name = request.Name,
            Input = request.Input,
            Variables = request.Variables,
            TriggerStepId = request.TriggerStepId,
            StepHandle = request.StepHandle,
            Properties = request.Properties,
            ParentId = request.ParentId
        };

        var runPipelineResponse = await pipelineClient.CreateAndRunInstanceAsync(createPipelineInstanceRequest, cancellationToken).ConfigureAwait(false);
        return new()
        {
            CannotStart = false,
            PipelineInstanceId = runPipelineResponse.PipelineInstanceId,
            Status = runPipelineResponse.Status,
            SubStatus = runPipelineResponse.SubStatus,
            Bookmarks = runPipelineResponse.Bookmarks,
            Incidents = runPipelineResponse.Incidents
        };
    }

    private async Task<Pipeline> GetPipelineAsync(StartPipelineRequest request, CancellationToken cancellationToken)
    {
        if (request.Pipeline != null)
            return request.Pipeline;

        if (request.PipelineDefinitionHandle == null)
            throw new InvalidOperationException("A pipeline definition handle must be provided.");

        var pipelineGraph = await pipelineDefinitionService.FindPipelineGraphAsync(request.PipelineDefinitionHandle, cancellationToken).ConfigureAwait(false);

        if (pipelineGraph == null)
            throw new PipelineGraphNotFoundException("Pipeline definition not found.", request.PipelineDefinitionHandle);

        return pipelineGraph.Pipeline;
    }
}