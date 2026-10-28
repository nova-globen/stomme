using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;

/// <summary>
/// Contains extension methods for <see cref="ICollection{Connection}"/>.
/// </summary>
public static class ConnectionsExtensions
{
   extension(ICollection<Connection> connections)
   {
       /// <summary>
       /// Returns all inbound connections of the specified step.
       /// </summary>
       public IEnumerable<Connection> InboundConnections(IStep step) => connections.Where(x => x.Target.Step == step).Distinct().ToList();

       /// <summary>
       /// Returns all inbound steps of the specified step.
       /// </summary>
       public IEnumerable<IStep> InboundSteps(IStep step) => connections.InboundConnections(step).Select(x => x.Source.Step);

       /// <summary>
       /// Returns all outbound connections of the specified step.
       /// </summary>
       public IEnumerable<Connection> OutboundConnections(IStep step) => connections.Where(x => x.Source.Step == step).Distinct().ToList();

       /// <summary>
       /// Returns all outbound connections of the specified step matching the specified outcomes.
       /// </summary>
       public IEnumerable<Connection> OutboundConnections(IStep step, Outcomes outcomes) => connections.OutboundConnections(step).Where(c => outcomes.Names.Contains(c.Source.Port));

       /// <summary>
       /// Returns all outbound steps of the specified step.
       /// </summary>
       public IEnumerable<IStep> OutboundSteps(IStep step) => connections.OutboundConnections(step).Select(x => x.Source.Step);

       /// <summary>
       /// Returns all outbound steps of the specified step matching the specified outcomes
       /// </summary>
       public IEnumerable<IStep> OutboundSteps(IStep step, Outcomes outcomes) => connections.OutboundConnections(step).Where(c => outcomes.Names.Contains(c.Source.Port)).Select(x => x.Source.Step);
   }
}