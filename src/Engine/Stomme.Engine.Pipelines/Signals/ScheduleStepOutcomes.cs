namespace Stomme.Engine.Pipelines.Signals;

/// <summary>
/// Signaled when a step requests the scheduling of the specified set of outcomes.
/// </summary>
/// <param name="Outcomes">The outcomes to schedule.</param>
public record ScheduleStepOutcomes(params string[] Outcomes);