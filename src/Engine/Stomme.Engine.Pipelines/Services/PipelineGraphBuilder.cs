using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class PipelineGraphBuilder(IStepVisitor stepVisitor, IIdentityGraphService identityGraphService) : IPipelineGraphBuilder
{
    /// <inheritdoc />
    public async Task<PipelineGraph> BuildAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        var graph = await stepVisitor.VisitAsync(pipeline, cancellationToken).ConfigureAwait(false);
        var nodes = graph.Flatten().ToList();
        
        await identityGraphService.AssignIdentitiesAsync(nodes).ConfigureAwait(false);
        return new PipelineGraph(pipeline, graph, nodes);
    }
}