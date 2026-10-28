using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Runtime.Stimuli;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

public static class StepExecutionContextEventExtensions
{
    /// <summary>
    /// Suspends the current step's execution and waits for a specified event to occur before continuing.
    /// </summary>
    /// <param name="context">The step execution context associated with the current step execution.</param>
    /// <param name="eventName">The name of the event to wait for.</param>
    /// <param name="callback">An optional delegate to execute when the event occurs.</param>
    public static void WaitForEvent(this StepExecutionContext context, string eventName, ExecuteStepDelegate? callback = null)
    {
        if (context.IsTriggerOfPipeline())
        {
            callback?.Invoke(context);
            return;
        }

        var options = new CreateBookmarkArgs
        {
            Stimulus = new EventStimulus(eventName),
            IncludeStepInstanceId = false,
            BookmarkName = RuntimeStimulusNames.Event,
            Callback = callback
        };
        context.CreateBookmark(options);
    }
    
    public static EventStimulus GetEventStimulus(this TriggerIndexingContext context, string eventName)
    {
        context.TriggerName = RuntimeStimulusNames.Event;
        return new(eventName);
    }

    public static object? GetEventInput(this StepExecutionContext context)
    {
        return context.TryGetPipelineInput<object?>(Event.EventInputPipelineInputKey, out var input) ? input : null;
    }
}