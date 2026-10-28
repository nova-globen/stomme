using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Services;

internal record UpdatedPipelineDefinition(PipelineDefinition Definition, PipelineGraph NewGraph);

public class PipelineReferenceUpdater(
    IPipelineDefinitionPublisher publisher,
    IPipelineDefinitionService pipelineDefinitionService,
    IPipelineDefinitionStore pipelineDefinitionStore,
    IPipelineReferenceGraphBuilder pipelineReferenceGraphBuilder,
    PipelineDefinitionStepDescriptorFactory pipelineDefinitionStepDescriptorFactory,
    IStepRegistry stepRegistry,
    IApiSerializer serializer)
    : IPipelineReferenceUpdater
{
    private bool _isUpdating;

    public async Task<UpdatePipelineReferencesResult> UpdatePipelineReferencesAsync(
        PipelineDefinition referencedDefinition,
        CancellationToken cancellationToken = default)
    {
        if (_isUpdating ||
            referencedDefinition.Options is not { UsableAsStep: true, AutoUpdateConsumingPipelines: true })
            return new([]);

        var referenceGraph = await pipelineReferenceGraphBuilder.BuildGraphAsync(referencedDefinition.DefinitionId, cancellationToken).ConfigureAwait(false);
        
        // Get all consumer (source) and referenced (target) IDs from the edges
        var referencingIds = referenceGraph.Edges.Select(e => e.Source).Distinct().ToList();
        var referencedIds = referenceGraph.Edges.Select(e => e.Target).Distinct().ToList();

        if (referencingIds.Count == 0)
            return new([]);

        var referencingPipelineGraphs = (await pipelineDefinitionService.FindPipelineGraphsAsync(new()
            {
                DefinitionIds = referencingIds,
                VersionOptions = VersionOptions.Latest,
                IsReadonly = false
            }, cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => g.Pipeline.Identity.DefinitionId);

        var referencedPipelineDefinitionList = (await pipelineDefinitionStore.FindManyAsync(new()
        {
            DefinitionIds = referencedIds,
            VersionOptions = VersionOptions.Published,
            IsReadonly = false
        }, cancellationToken).ConfigureAwait(false)).ToList();

        var referencedPipelineDefinitionsPublished = referencedPipelineDefinitionList
            .GroupBy(x => x.DefinitionId)
            .Select(group =>
            {
                var publishedVersion = group.FirstOrDefault(x => x.IsPublished);
                return publishedVersion ?? group.First();
            })
            .ToDictionary(d => d.DefinitionId);

        var initialPublicationState = new Dictionary<string, bool>();

        foreach (var pipelineGraph in referencingPipelineGraphs)
            initialPublicationState[pipelineGraph.Key] = pipelineGraph.Value.Pipeline.Publication.IsPublished;

        // Add the initially referenced definition
        referencedPipelineDefinitionsPublished[referencedDefinition.DefinitionId] = referencedDefinition;

        // Use the OutboundEdges lookup from the graph (Source → what it depends on)
        var dependencyMap = referenceGraph.OutboundEdges;

        // Perform topological sort to ensure dependent pipelines are processed in the right order
        var sortedPipelineIds = referencingIds
            .TSort(id => dependencyMap[id], true)
            // Only process pipelines that exist in our referencing pipelines dictionary
            .Where(id => referencingPipelineGraphs.ContainsKey(id))
            .ToList();

        var updatedPipelines = new Dictionary<string, UpdatedPipelineDefinition>();

        // Create a cache for drafts that we've already created during this operation
        var draftCache = new Dictionary<string, PipelineDefinition>();

        foreach (var id in sortedPipelineIds)
        {
            if (!referencingPipelineGraphs.TryGetValue(id, out var graph) || !dependencyMap[id].Any())
                continue;

            foreach (var refId in dependencyMap[id])
            {
                var target = referencedPipelineDefinitionsPublished.GetValueOrDefault(refId);
                if (target == null) continue;

                var updated = await UpdatePipelineAsync(graph, target, draftCache, initialPublicationState, cancellationToken).ConfigureAwait(false);
                if (updated == null) continue;

                graph = updated.NewGraph;
                updatedPipelines[updated.Definition.DefinitionId] = updated;
                referencedPipelineDefinitionsPublished[id] = updated.Definition;
                draftCache[id] = updated.Definition;
                referencingPipelineGraphs[id] = updated.NewGraph;
            }
        }

        _isUpdating = true;
        foreach (var updatedPipeline in updatedPipelines.Values)
        {
            var requiresPublication = initialPublicationState.GetValueOrDefault(updatedPipeline.Definition.DefinitionId);
            if (requiresPublication)
                await publisher.PublishAsync(updatedPipeline.Definition, cancellationToken).ConfigureAwait(false);
            else
                await publisher.SaveDraftAsync(updatedPipeline.Definition, cancellationToken).ConfigureAwait(false);
        }

        _isUpdating = false;

        return new(updatedPipelines.Select(u => u.Value.Definition));
    }


    private async Task<UpdatedPipelineDefinition?> UpdatePipelineAsync(
        PipelineGraph graph,
        PipelineDefinition target,
        Dictionary<string, PipelineDefinition> draftCache,
        Dictionary<string, bool> initialPublicationState,
        CancellationToken cancellationToken)
    {
        var willTargetBePublished = initialPublicationState.GetValueOrDefault(target.DefinitionId, target.IsPublished);
        if (!willTargetBePublished)
            return null;

        var id = graph.Pipeline.Identity.DefinitionId;
        var draft = await GetOrCreateDraftAsync(id, draftCache, cancellationToken).ConfigureAwait(false);
        if (draft == null) return null;

        var newGraph = await pipelineDefinitionService.MaterializePipelineAsync(draft, cancellationToken).ConfigureAwait(false);
        var outdated = FindSteps(newGraph.Root, target.DefinitionId)
            .Where(a => a.PipelineDefinitionVersionId != target.Id)
            .ToList();

        if (!outdated.Any()) return null;

        foreach (var act in outdated)
        {
            act.PipelineDefinitionVersionId = target.Id;
            act.Version = target.Version;
            act.LatestAvailablePublishedVersionId = target.Id;
            act.LatestAvailablePublishedVersion = target.Version;
        }

        if (newGraph.Root.Step is Pipeline wf)
            draft.StringData = serializer.Serialize(wf.Root);

        return new(draft, newGraph);
    }

    private async Task<PipelineDefinition?> GetOrCreateDraftAsync(
        string definitionId,
        Dictionary<string, PipelineDefinition> draftCache,
        CancellationToken cancellationToken)
    {
        // Check if we already have a draft for this pipeline
        if (draftCache.TryGetValue(definitionId, out var cachedDraft))
            return cachedDraft;

        // Create or get a draft for this pipeline
        var draft = await publisher.GetDraftAsync(definitionId, VersionOptions.Latest, cancellationToken).ConfigureAwait(false);
        if (draft == null) return null;

        // Store the draft in the cache for potential future use
        draftCache[definitionId] = draft;

        // Get the current published version of the pipeline definition.
        var publishedVersion = await pipelineDefinitionStore.FindAsync(
            PipelineDefinitionHandle.ByDefinitionId(definitionId, VersionOptions.Published).ToFilter(),
            cancellationToken).ConfigureAwait(false);

        // Update the step registry to be able to materialize the pipeline.
        var stepDescriptor = pipelineDefinitionStepDescriptorFactory.CreateDescriptor(draft, publishedVersion);
        stepRegistry.Add(typeof(PipelineDefinitionStepProvider), stepDescriptor);

        return draft;
    }

    private static IEnumerable<PipelineDefinitionStep> FindSteps(StepNode node, string definitionId)
    {
        // Do not drill into steps that are PipelineDefinitionStep
        if (node.Step is PipelineDefinitionStep)
            yield break;

        foreach (var child in node.Children)
        {
            if (child.Step is PipelineDefinitionStep step && step.PipelineDefinitionId == definitionId)
                yield return step;
            foreach (var grandChildStep in FindSteps(child, definitionId))
                yield return grandChildStep;
        }
    }
}