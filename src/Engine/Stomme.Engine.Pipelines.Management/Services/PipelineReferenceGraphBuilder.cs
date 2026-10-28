using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Options;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <summary>
/// Default implementation of <see cref="IPipelineReferenceGraphBuilder"/> that uses <see cref="IPipelineReferenceQuery"/>
/// to recursively build a complete graph of pipeline references.
/// </summary>
public class PipelineReferenceGraphBuilder(IPipelineReferenceQuery pipelineReferenceQuery, IOptions<PipelineReferenceGraphOptions> options) : IPipelineReferenceGraphBuilder
{
    private readonly PipelineReferenceGraphOptions _options = options.Value;
    /// <inheritdoc />
    public async Task<PipelineReferenceGraph> BuildGraphAsync(string definitionId, CancellationToken cancellationToken = default)
    {
        var edges = await BuildEdgesAsync(definitionId, cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new([definitionId], edges);
    }

    /// <inheritdoc />
    public async Task<PipelineReferenceGraph> BuildGraphAsync(IEnumerable<string> definitionIds, CancellationToken cancellationToken = default)
    {
        var allEdges = new List<PipelineReferenceEdge>();
        var visitedIds = new HashSet<string>();
        var rootIds = definitionIds.ToList();

        foreach (var definitionId in rootIds)
        {
            await foreach (var edge in BuildEdgesAsync(definitionId, cancellationToken, visitedIds).ConfigureAwait(false))
            {
                allEdges.Add(edge);
            }
        }

        // Deduplicate edges
        var distinctEdges = allEdges.Distinct().ToList();

        return new(rootIds, distinctEdges);
    }

    private async IAsyncEnumerable<PipelineReferenceEdge> BuildEdgesAsync(
        string definitionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        HashSet<string>? visitedIds = null,
        int currentDepth = 0)
    {
        visitedIds ??= new();

        // Check depth limit
        if (_options.MaxDepth > 0 && currentDepth >= _options.MaxDepth)
            yield break;

        // Check max definitions limit
        if (_options.MaxDefinitions > 0 && visitedIds.Count >= _options.MaxDefinitions)
            yield break;

        // If we've already processed this definition ID, skip it to prevent infinite recursion.
        if (!visitedIds.Add(definitionId))
            yield break;

        var consumerIds = (await pipelineReferenceQuery.ExecuteAsync(definitionId, cancellationToken).ConfigureAwait(false)).ToList();
        
        // For each consumer, create an edge: Consumer (Source) → definitionId (Target)
        foreach (var consumerId in consumerIds)
        {
            yield return new(Source: consumerId, Target: definitionId);
            
            // Recursively process the consumer
            await foreach (var childEdge in BuildEdgesAsync(consumerId, cancellationToken, visitedIds, currentDepth + 1).ConfigureAwait(false))
                yield return childEdge;
        }
    }
}

