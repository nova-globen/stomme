using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Exceptions;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Mappers;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Runtime.Exceptions;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a client for executing and managing local pipelines.
/// </summary>
public class LocalPipelineClient(
    string pipelineInstanceId,
    IPipelineInstanceManager pipelineInstanceManager,
    IPipelineDefinitionService pipelineDefinitionService,
    IPipelineRunner pipelineRunner,
    IPipelineCanceler pipelineCanceler,
    PipelineStateMapper pipelineStateMapper,
    IBookmarkManager bookmarkManager,
    ILogger<LocalPipelineClient> logger) : IPipelineClient
{
    /// <inheritdoc />
    public string PipelineInstanceId => pipelineInstanceId;

    /// <inheritdoc />
    public async Task<CreatePipelineInstanceResponse> CreateInstanceAsync(CreatePipelineInstanceRequest request, CancellationToken cancellationToken = default)
    {
        var pipelineDefinitionHandle = request.PipelineDefinitionHandle;
        var pipelineGraph = await GetPipelineGraphAsync(pipelineDefinitionHandle, cancellationToken).ConfigureAwait(false);

        var options = new PipelineInstanceOptions
        {
            PipelineInstanceId = PipelineInstanceId,
            CorrelationId = request.CorrelationId,
            Name = request.Name,
            ParentPipelineInstanceId = request.ParentId,
            Input = request.Input,
            Properties = request.Properties
        };

        await pipelineInstanceManager.CreateAndCommitPipelineInstanceAsync(pipelineGraph.Pipeline, options, cancellationToken).ConfigureAwait(false);
        return new();
    }

    /// <inheritdoc />
    public async Task<RunPipelineInstanceResponse> RunInstanceAsync(RunPipelineInstanceRequest request, CancellationToken cancellationToken = default)
    {
        var pipelineInstance = await GetPipelineInstanceAsync(cancellationToken).ConfigureAwait(false);
        return await RunInstanceAsync(pipelineInstance, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineInstanceResponse> CreateAndRunInstanceAsync(CreateAndRunPipelineInstanceRequest request, CancellationToken cancellationToken = default)
    {
        var createRequest = new CreatePipelineInstanceRequest
        {
            Properties = request.Properties,
            CorrelationId = request.CorrelationId,
            Name = request.Name,
            Input = request.Input,
            PipelineDefinitionHandle = request.PipelineDefinitionHandle,
            ParentId = request.ParentId
        };
        var pipelineInstance = await CreateInstanceInternalAsync(createRequest, cancellationToken).ConfigureAwait(false);
        return await RunInstanceAsync(pipelineInstance, new()
        {
            Input = request.Input,
            Variables = request.Variables,
            Properties = request.Properties,
            TriggerStepId = request.TriggerStepId,
            StepHandle = request.StepHandle,
            IncludePipelineOutput = request.IncludePipelineOutput,
            SchedulingStepExecutionId = request.SchedulingStepExecutionId,
            SchedulingPipelineInstanceId = request.SchedulingPipelineInstanceId,
            SchedulingCallStackDepth = request.SchedulingCallStackDepth
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        var pipelineInstance = await GetPipelineInstanceAsync(cancellationToken).ConfigureAwait(false);
        await CancelAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
    }

    private async Task CancelAsync(PipelineInstance pipelineInstance, CancellationToken cancellationToken)
    {
        if (pipelineInstance.Status != PipelineStatus.Running) return;
        var pipelineGraph = await GetPipelineGraphAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        var pipelineState = await pipelineCanceler.CancelPipelineAsync(pipelineGraph, pipelineInstance.PipelineState, cancellationToken).ConfigureAwait(false);
        await pipelineInstanceManager.SaveAsync(pipelineState, cancellationToken).ConfigureAwait(false);

        // Stomme BL-229: what the run waited on goes with it. The canceller ends the run's state but no commit diffs its
        // bookmarks, so they stayed stored - a cancelled run's approval was listed as waiting, a decision on it answered
        // "Approved" (the run itself refused to go on), and a due wait was resumed again at every tick (measured).
        await bookmarkManager.DeleteManyAsync(new BookmarkFilter { PipelineInstanceId = pipelineInstance.Id }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineState> ExportStateAsync(CancellationToken cancellationToken = default)
    {
        var pipelineInstance = await GetPipelineInstanceAsync(cancellationToken).ConfigureAwait(false);
        return pipelineInstance.PipelineState;
    }

    /// <inheritdoc />
    public async Task ImportStateAsync(PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        var pipelineInstance = pipelineStateMapper.Map(pipelineState)!;
        await pipelineInstanceManager.SaveAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> InstanceExistsAsync(CancellationToken cancellationToken = default)
    {
        return pipelineInstanceManager.ExistsAsync(pipelineInstanceId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
    {
        // Load the pipeline instance (single DB call)
        var pipelineInstance = await TryGetPipelineInstanceAsync(cancellationToken).ConfigureAwait(false);
        if (pipelineInstance == null)
            return false;

        await CancelAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        
        // Delete the pipeline instance
        var filter = new PipelineInstanceFilter { Id = pipelineInstanceId };
        await pipelineInstanceManager.DeleteAsync(filter, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<RunPipelineInstanceResponse> RunInstanceAsync(PipelineInstance pipelineInstance, RunPipelineInstanceRequest request, CancellationToken cancellationToken = default)
    {
        var pipelineState = pipelineInstance.PipelineState;

        if (pipelineInstance.Status != PipelineStatus.Running)
        {
            logger.LogWarning("Attempt to resume pipeline {PipelineInstanceId} that is not in the Running state. The actual state is {ActualPipelineStatus}", pipelineState.Id, pipelineState.Status);
            return new()
            {
                PipelineInstanceId = PipelineInstanceId,
                Status = pipelineInstance.Status,
                SubStatus = pipelineInstance.SubStatus
            };
        }

        var runPipelineOptions = new RunPipelineOptions
        {
            Input = request.Input,
            Variables = request.Variables,
            Properties = request.Properties,
            BookmarkId = request.BookmarkId,
            TriggerStepId = request.TriggerStepId,
            StepHandle = request.StepHandle,
            SchedulingStepExecutionId = request.SchedulingStepExecutionId,
            SchedulingPipelineInstanceId = request.SchedulingPipelineInstanceId,
            SchedulingCallStackDepth = request.SchedulingCallStackDepth
        };

        var pipelineGraph = await GetPipelineGraphAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        var pipelineResult = await pipelineRunner.RunAsync(pipelineGraph, pipelineState, runPipelineOptions, cancellationToken).ConfigureAwait(false);

        pipelineState = pipelineResult.PipelineState;

        return new()
        {
            PipelineInstanceId = PipelineInstanceId,
            Status = pipelineState.Status,
            SubStatus = pipelineState.SubStatus,
            Incidents = pipelineState.Incidents,
            Output = request.IncludePipelineOutput ? new Dictionary<string, object>(pipelineState.Output) : null,
            Bookmarks = pipelineState.Bookmarks
        };
    }
    
    public async Task<PipelineInstance> CreateInstanceInternalAsync(CreatePipelineInstanceRequest request, CancellationToken cancellationToken = default)
    {
        var pipelineDefinitionHandle = request.PipelineDefinitionHandle;
        var pipelineGraph = await GetPipelineGraphAsync(pipelineDefinitionHandle, cancellationToken).ConfigureAwait(false);

        var options = new PipelineInstanceOptions
        {
            PipelineInstanceId = PipelineInstanceId,
            CorrelationId = request.CorrelationId,
            Name = request.Name,
            ParentPipelineInstanceId = request.ParentId,
            Input = request.Input,
            Properties = request.Properties
        };

        return pipelineInstanceManager.CreatePipelineInstance(pipelineGraph.Pipeline, options);
    }

    private async Task<PipelineInstance> GetPipelineInstanceAsync(CancellationToken cancellationToken)
    {
        var pipelineInstance = await TryGetPipelineInstanceAsync(cancellationToken).ConfigureAwait(false);
        if (pipelineInstance == null) throw new PipelineInstanceNotFoundException("Pipeline instance not found.", PipelineInstanceId);
        return pipelineInstance;
    }

    private Task<PipelineInstance?> TryGetPipelineInstanceAsync(CancellationToken cancellationToken)
    {
        return pipelineInstanceManager.FindByIdAsync(PipelineInstanceId, cancellationToken);
    }

    private async Task<PipelineGraph> GetPipelineGraphAsync(PipelineInstance pipelineInstance, CancellationToken cancellationToken)
    {
        var handle = PipelineDefinitionHandle.ByDefinitionVersionId(pipelineInstance.DefinitionVersionId);
        return await GetPipelineGraphAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PipelineGraph> GetPipelineGraphAsync(PipelineDefinitionHandle definitionHandle, CancellationToken cancellationToken)
    {
        var result = await pipelineDefinitionService.TryFindPipelineGraphAsync(definitionHandle, cancellationToken).ConfigureAwait(false);
        if (!result.PipelineDefinitionExists) throw new PipelineDefinitionNotFoundException("Pipeline definition not found.", definitionHandle);
        if (!result.PipelineGraphExists) throw new PipelineMaterializerNotFoundException(result.PipelineDefinition!.MaterializerName);
        return result.PipelineGraph!;
    }
}