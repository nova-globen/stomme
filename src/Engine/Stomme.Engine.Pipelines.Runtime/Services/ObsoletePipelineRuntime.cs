
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Matches;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Parameters;
using Stomme.Engine.Pipelines.Runtime.Params;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Results;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.DependencyInjection;
using Open.Linq.AsyncExtensions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Implements the now deprecated pipeline runtime API methods. 
/// </summary>
public class ObsoletePipelineRuntime(
    Func<string?, CancellationToken, ValueTask<IPipelineClient>> createClientAsync,
    IPipelineDefinitionService pipelineDefinitionService,
    IPipelineActivationStrategyEvaluator pipelineActivationStrategyEvaluator,
    IStimulusSender stimulusSender,
    IStimulusHasher stimulusHasher,
    IBookmarkStore bookmarkStore,
    IPipelineInstanceStore pipelineInstanceStore,
    ITriggerBoundPipelineService triggerBoundPipelineService,
    IBookmarkBoundPipelineService bookmarkBoundPipelineService)
{
    public static ObsoletePipelineRuntime Create(IServiceProvider serviceProvider, Func<string?, CancellationToken, ValueTask<IPipelineClient>> createClientAsync)
    {
        return ActivatorUtilities.CreateInstance<ObsoletePipelineRuntime>(serviceProvider, createClientAsync);
    }
    
    public async Task<CanStartPipelineResult> CanStartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = null)
    {
        var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
        var pipelineGraph = await pipelineDefinitionService.FindPipelineGraphAsync(definitionId, options?.VersionOptions ?? VersionOptions.Published, cancellationToken).ConfigureAwait(false);
        var pipeline = pipelineGraph!.Pipeline;

        var canStart = await pipelineActivationStrategyEvaluator.CanStartPipelineAsync(new()
        {
            Pipeline = pipeline,
            CorrelationId = options?.CorrelationId,
            CancellationToken = cancellationToken
        }).ConfigureAwait(false);

        return new(null, canStart);
    }

    public async Task<PipelineExecutionResult> StartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = null)
    {
        var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
        var client = await createClientAsync(options?.InstanceId, cancellationToken).ConfigureAwait(false);
        var createRequest = new CreateAndRunPipelineInstanceRequest
        {
            Properties = options?.Properties,
            CorrelationId = options?.CorrelationId,
            Input = options?.Input,
            PipelineDefinitionHandle = PipelineDefinitionHandle.ByDefinitionId(definitionId, options?.VersionOptions ?? VersionOptions.Published),
            ParentId = options?.ParentPipelineInstanceId,
            TriggerStepId = options?.TriggerStepId
        };
        var response = await client.CreateAndRunInstanceAsync(createRequest, cancellationToken).ConfigureAwait(false);
        return new(response.PipelineInstanceId, response.Status, response.SubStatus, response.Bookmarks, response.Incidents, null, new Dictionary<string, object>());
    }

    public async Task<ICollection<PipelineExecutionResult>> StartPipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = null)
    {
        var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
        var metadata = new StimulusMetadata
        {
            CorrelationId = options?.CorrelationId,
            PipelineInstanceId = options?.PipelineInstanceId,
            Properties = options?.Properties,
            StepInstanceId = options?.StepInstanceId,
            Input = options?.Input
        };
        var result = await stimulusSender.SendAsync(stepTypeName, bookmarkPayload, metadata, cancellationToken).ConfigureAwait(false);
        var results = result.PipelineInstanceResponses.Select(x => new PipelineExecutionResult(x.PipelineInstanceId, x.Status, x.SubStatus, x.Bookmarks, x.Incidents, null, new Dictionary<string, object>())).ToList();
        return results;
    }

    public async Task<PipelineExecutionResult?> TryStartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = null)
    {
        return await StartPipelineAsync(definitionId, options).ConfigureAwait(false);
    }

    public async Task<PipelineExecutionResult?> ResumePipelineAsync(string pipelineInstanceId, ResumePipelineRuntimeParams? options = null)
    {
        var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
        var pipelineClient = await createClientAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);
        var exists = await pipelineClient.InstanceExistsAsync(cancellationToken).ConfigureAwait(false);

        if (!exists)
            return null;

        var runPipelineRequest = new RunPipelineInstanceRequest
        {
            Input = options?.Input,
            Properties = options?.Properties,
            StepHandle = options?.StepHandle,
            BookmarkId = options?.BookmarkId
        };

        var response = await pipelineClient.RunInstanceAsync(runPipelineRequest, cancellationToken).ConfigureAwait(false);

        return new(response.PipelineInstanceId, response.Status, response.SubStatus, response.Bookmarks, response.Incidents,null, new Dictionary<string, object>());
    }

    public async Task<ICollection<PipelineExecutionResult>> ResumePipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = null)
    {
        var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
        var metadata = new StimulusMetadata
        {
            CorrelationId = options?.CorrelationId,
            PipelineInstanceId = options?.PipelineInstanceId,
            Properties = options?.Properties,
            StepInstanceId = options?.StepInstanceId,
            Input = options?.Input
        };
        var result = await stimulusSender.SendAsync(stepTypeName, bookmarkPayload, metadata, cancellationToken).ConfigureAwait(false);
        var results = result.PipelineInstanceResponses.Select(x => new PipelineExecutionResult(x.PipelineInstanceId, x.Status, x.SubStatus, x.Bookmarks, x.Incidents, null, new Dictionary<string, object>())).ToList();
        return results;
    }

    public async Task<TriggerPipelinesResult> TriggerPipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = null)
    {
        var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
        var metadata = new StimulusMetadata
        {
            CorrelationId = options?.CorrelationId,
            PipelineInstanceId = options?.PipelineInstanceId,
            Properties = options?.Properties,
            StepInstanceId = options?.StepInstanceId,
            Input = options?.Input
        };
        var result = await stimulusSender.SendAsync(stepTypeName, bookmarkPayload, metadata, cancellationToken).ConfigureAwait(false);
        var results = result.PipelineInstanceResponses.Select(x => new PipelineExecutionResult(x.PipelineInstanceId, x.Status, x.SubStatus, x.Bookmarks, x.Incidents, null, new Dictionary<string, object>())).ToList();
        return new(results);
    }

    public async Task<PipelineExecutionResult> ExecutePipelineAsync(PipelineMatch match, ExecutePipelineParams? options = default)
    {
        var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
        if (match is StartablePipelineMatch collectedStartablePipeline)
        {
            var startOptions = new StartPipelineRuntimeParams
            {
                CorrelationId = collectedStartablePipeline.CorrelationId,
                Input = options?.Input,
                Properties = options?.Properties,
                VersionOptions = VersionOptions.Published,
                TriggerStepId = collectedStartablePipeline.StepId,
                CancellationToken = cancellationToken
            };

            var startResult = await StartPipelineAsync(collectedStartablePipeline.DefinitionId!, startOptions).ConfigureAwait(false);
            return startResult with
            {
                TriggeredStepId = collectedStartablePipeline.StepId
            };
        }

        var collectedResumablePipeline = (match as ResumablePipelineMatch)!;
        var runtimeOptions = new ResumePipelineRuntimeParams
        {
            CorrelationId = collectedResumablePipeline.CorrelationId,
            BookmarkId = collectedResumablePipeline.BookmarkId,
            Input = options?.Input,
            Properties = options?.Properties,
            CancellationToken = cancellationToken,
        };

        return (await ResumePipelineAsync(collectedResumablePipeline.PipelineInstanceId, runtimeOptions).ConfigureAwait(false))!;
    }

    public async Task<CancellationResult> CancelPipelineAsync(string pipelineInstanceId, CancellationToken cancellationToken = default)
    {
        var client = await createClientAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);
        await client.CancelAsync(cancellationToken).ConfigureAwait(false);
        return new(true);
    }

    public async Task<IEnumerable<PipelineMatch>> FindPipelinesAsync(PipelinesFilter filter, CancellationToken cancellationToken = default)
    {
        var startablePipelines = await FindStartablePipelinesAsync(filter, cancellationToken).ConfigureAwait(false);
        var resumablePipelines = await FindResumablePipelinesAsync(filter, cancellationToken).ConfigureAwait(false);
        var results = startablePipelines.Concat(resumablePipelines).ToList();
        return results;
    }

    public async Task<PipelineState?> ExportPipelineStateAsync(string pipelineInstanceId, CancellationToken cancellationToken = default)
    {
        var client = await createClientAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);
        return await client.ExportStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportPipelineStateAsync(PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        var client = await createClientAsync(pipelineState.Id, cancellationToken).ConfigureAwait(false);
        await client.ImportStateAsync(pipelineState, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateBookmarkAsync(StoredBookmark bookmark, CancellationToken cancellationToken = default)
    {
        await bookmarkStore.SaveAsync(bookmark, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CountRunningPipelinesAsync(CountRunningPipelinesRequest request, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineInstanceFilter
        {
            DefinitionId = request.DefinitionId,
            Version = request.Version,
            CorrelationId = request.CorrelationId,
            PipelineStatus = PipelineStatus.Running
        };
        return await pipelineInstanceStore.CountAsync(filter, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IEnumerable<PipelineMatch>> FindStartablePipelinesAsync(PipelinesFilter filter, CancellationToken cancellationToken = default)
    {
        var stimulusHash = stimulusHasher.Hash(filter.StepTypeName, filter.BookmarkPayload, filter.Options.StepInstanceId);
        var triggerBoundPipelines = await triggerBoundPipelineService.FindManyAsync(stimulusHash, cancellationToken).ToList().ConfigureAwait(false);
        var correlationId = filter.Options.CorrelationId;

        var query =
                from triggerBoundPipeline in triggerBoundPipelines
                from trigger in triggerBoundPipeline.Triggers
                select new StartablePipelineMatch(correlationId, trigger.StepId, triggerBoundPipeline.PipelineGraph.Pipeline.Identity.DefinitionId, filter.BookmarkPayload);

        return query.ToList();
    }

    private async Task<IEnumerable<PipelineMatch>> FindResumablePipelinesAsync(PipelinesFilter filter, CancellationToken cancellationToken)
    {
        var bookmarkOptions = new FindBookmarkOptions
        {
            CorrelationId = filter.Options.CorrelationId,
            PipelineInstanceId = filter.Options.PipelineInstanceId,
            StepInstanceId = filter.Options.StepInstanceId
        };
        var bookmarkBoundPipelines = await bookmarkBoundPipelineService.FindManyAsync(filter.StepTypeName, filter.BookmarkPayload, bookmarkOptions, cancellationToken).ToList().ConfigureAwait(false);

        return (
                from bookmarkBoundPipeline in bookmarkBoundPipelines
                from bookmark in bookmarkBoundPipeline.Bookmarks
                select new ResumablePipelineMatch(bookmarkBoundPipeline.PipelineInstanceId, bookmark.CorrelationId, bookmark.Id, bookmark.Payload))
            .ToList();
    }
}