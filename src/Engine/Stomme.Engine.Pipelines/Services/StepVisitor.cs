using StepNode = Stomme.Engine.Pipelines.Models.StepNode;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class StepVisitor : IStepVisitor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEnumerable<IStepResolver> _portResolvers;

    /// <summary>
    /// Initializes a new instance of the <see cref="StepVisitor"/> class.
    /// </summary>
    public StepVisitor(IEnumerable<IStepResolver> portResolvers, IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _portResolvers = portResolvers.OrderByDescending(x => x.Priority).ToList();
    }

    /// <inheritdoc />
    public async Task<StepNode> VisitAsync(IStep step, CancellationToken cancellationToken = default)
    {
        var graph = new StepNode(step, "Root");
        var collectedNodes = new HashSet<StepNode>([
            graph
        ]);
        var collectedSteps = new HashSet<IStep>([
            step
        ]);
        var visitorContext = new StepVisitorContext
        {
            CollectedSteps = collectedSteps,
            CollectedNodes = collectedNodes
        };

        await VisitRecursiveAsync((graph, step), visitorContext, cancellationToken).ConfigureAwait(false);
        return graph;
    }

    private async Task VisitRecursiveAsync((StepNode Node, IStep Step) pair, StepVisitorContext visitorContext, CancellationToken cancellationToken)
    {
        if (pair.Step is IInitializable initializable)
        {
            var context = new InitializationContext(_serviceProvider, cancellationToken);
            await initializable.InitializeAsync(context).ConfigureAwait(false);
        }

        await VisitPortsRecursiveAsync(pair, visitorContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task VisitPortsRecursiveAsync((StepNode Node, IStep Step) pair, StepVisitorContext visitorContext, CancellationToken cancellationToken)
    {
        var resolver = _portResolvers.FirstOrDefault(x => x.GetSupportsStep(pair.Step));

        if (resolver == null)
            return;

        var stepPorts = await resolver.GetStepPortsAsync(pair.Step, cancellationToken).ConfigureAwait(false);
        var collectedSteps = visitorContext.CollectedSteps;
        var collectedNodes = visitorContext.CollectedNodes;

        foreach (var stepPort in stepPorts)
        {
            foreach (var step in stepPort.GetSteps())
            {
                // Continue if the specified step was already encountered.
                if (collectedSteps.Contains(step))
                    continue;

                var childNode = collectedNodes.FirstOrDefault(x => x.Step == step);

                if (childNode == null)
                {
                    childNode = new(step, stepPort.PortName);
                    collectedNodes.Add(childNode);
                }

                childNode.AddParent(pair.Node);
                pair.Node.AddChild(childNode);
                collectedSteps.Add(step);
                await VisitRecursiveAsync((childNode, step), visitorContext, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private class StepVisitorContext
    {
        public HashSet<IStep> CollectedSteps { get; set; } = [];
        public HashSet<StepNode> CollectedNodes { get; set; } = [];
    }
}