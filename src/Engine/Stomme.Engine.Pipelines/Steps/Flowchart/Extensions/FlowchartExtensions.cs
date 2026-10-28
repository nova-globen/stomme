namespace Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;

public static class FlowchartExtensions
{
    public static IStep? GetRootStep(this Steps.Flowchart flowchart)
    {
        // Get the first step that has no inbound connections - one that starts a run only when named never is.
        var query =
            from step in flowchart.Steps
            let inboundConnections = flowchart.Connections.Any(x => x.Target.Step == step)
            where !inboundConnections && step is not IStartsOnlyWhenNamed
            select step;

        var rootStep = query.FirstOrDefault();
        return rootStep;
    }
}