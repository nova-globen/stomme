namespace Stomme.Engine.Pipelines;

/// <summary>
/// Implemented by trigger steps that generate events. For example, a timer step is an event generator. 
/// </summary>
/// <remarks>
/// When an event generator step is marked as startable, it generates new pipeline instances. Otherwise, it schedules new pipeline executions for the current pipeline instance, regardless of whether or not the step is currently blocking.
/// For example, a timer step will continuously generate events, causing the pipeline instance to execute the timer event and subsequent steps, even if the timer step wasn't scheduled with the pipeline execution stack.    
/// </remarks>
public interface IEventGenerator : ITrigger
{
}