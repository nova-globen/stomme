using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <summary>
/// Represents the default implementation of <see cref="IPipelineReferenceQuery"/> that queries pipelines
/// referencing a specific pipeline definition.
/// </summary>
/// <remarks>
/// This class is designed to identify pipelines that are dependent on a given pipeline definition.
/// It leverages services such as <see cref="IPipelineDefinitionService"/> and <see cref="IPipelineDefinitionStore"/>
/// to locate, graph, and analyze pipelines that include the specified pipeline definition.
/// </remarks>
public class DefaultPipelineReferenceQuery(IPipelineDefinitionService pipelineDefinitionService, IPipelineDefinitionStore pipelineDefinitionStore) : IPipelineReferenceQuery
{
    public async Task<IEnumerable<string>> ExecuteAsync(string pipelineDefinitionId, CancellationToken cancellationToken = default)
    {
        var pipelineGraphs = await FindPipelinesContainingUpdatedPipelineDefinitionAsync(pipelineDefinitionId, cancellationToken).ConfigureAwait(false);
        return pipelineGraphs.Select(x => x.Pipeline.Identity.DefinitionId).Distinct();
    }
    
    private async Task<IEnumerable<PipelineGraph>> FindPipelinesContainingUpdatedPipelineDefinitionAsync(string updatedPipelineDefinitionId, CancellationToken cancellationToken)
    {
        var allPipelineGraphs = (await GetAllPipelineGraphsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var filteredPipelineGraphs = allPipelineGraphs
            .Where(x => FindPipelineStepDefinitionStepNodes(x.Root).Any(y => y.PipelineDefinitionId == updatedPipelineDefinitionId))
            .ToList();

        return filteredPipelineGraphs;
    }

    private IEnumerable<PipelineDefinitionStep> FindPipelineStepDefinitionStepNodes(StepNode parent)
    {
        foreach (var child in parent.Children)
        {
            if (child.Step is PipelineDefinitionStep pipelineDefinitionStep)
                yield return pipelineDefinitionStep;
            else
            {
                foreach (var grandChild in FindPipelineStepDefinitionStepNodes(child))
                    yield return grandChild;
            }
        }
    }

    private async Task<IEnumerable<PipelineGraph>> GetAllPipelineGraphsAsync(CancellationToken cancellationToken)
    {
        var pipelineDefinitionFilter = new PipelineDefinitionFilter
        {
            VersionOptions = VersionOptions.LatestOrPublished,
            IsReadonly = false
        };
        var pipelineDefinitionSummaries = await pipelineDefinitionStore.FindSummariesAsync(pipelineDefinitionFilter, cancellationToken).ConfigureAwait(false);
        
        // If there are pipeline definition summaries with the same definition ID, only take the latest version.
        pipelineDefinitionSummaries = pipelineDefinitionSummaries
            .GroupBy(x => x.DefinitionId)
            .Select(x => x.OrderByDescending(y => y.Version).First())
            .ToList();
        
        var pipelineGraphs = new List<PipelineGraph>();

        foreach (var pipelineDefinitionSummary in pipelineDefinitionSummaries)
        {
            var pipelineGraph = await pipelineDefinitionService.FindPipelineGraphAsync(pipelineDefinitionSummary.Id, cancellationToken).ConfigureAwait(false);

            if (pipelineGraph != null)
                pipelineGraphs.Add(pipelineGraph);
        }

        return pipelineGraphs;
    }
}