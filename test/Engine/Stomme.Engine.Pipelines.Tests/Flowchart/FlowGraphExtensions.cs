using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines.Tests.Flowchart;

public static class FlowGraphExtensions
{
    public static void ValidateOutboundConnections(this FlowGraph flowGraph, List<string> expected, IStep step)
    {
        Assert.Equal(expected, flowGraph.GetOutboundConnections(step).Select(c => c.ToString()));
    }

    public static void ValidateForwardInboundConnections(this FlowGraph flowGraph, List<string> expected, IStep step)
    {
        Assert.Equal(expected, flowGraph.GetForwardInboundConnections(step).Select(c => c.ToString()));
    }
    public static void ValidateBackwardConnection(this FlowGraph flowGraph, bool expectedBackward, bool expectedValid, Connection connection)
    {
        var actualBackward = flowGraph.IsBackwardConnection(connection, out var actualValid);
        Assert.Equal(expectedBackward, actualBackward);
        Assert.Equal(expectedValid, actualValid);
    }

    public static void ValidateDanglingStep(this FlowGraph flowGraph, bool expected, Step step)
    {
        Assert.Equal(expected, flowGraph.IsDanglingStep(step));
    }

    public static void ValidateAncestorSteps(this FlowGraph flowGraph, List<Step> expected, Step step)
    {
        Assert.Equal(expected, flowGraph.GetAncestorSteps(step));
    }
}
