using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;

namespace Stomme.Engine.Pipelines.Runtime.Steps;

public abstract class EventBase : Trigger<object>;

public abstract class EventBase<TResult> : Trigger<TResult>
{
    protected abstract string GetEventName(ExpressionExecutionContext context);

    protected virtual ValueTask OnEventReceivedAsync(StepExecutionContext context, TResult? input)
    {
        OnEventReceived(context, input);
        return default;
    }

    protected virtual void OnEventReceived(StepExecutionContext context, TResult? input)
    {
    }

    protected override object GetTriggerPayload(TriggerIndexingContext context)
    {
        var eventName = GetEventName(context.ExpressionExecutionContext);
        return context.GetEventStimulus(eventName);
    }
    
    protected override ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var eventName = GetEventName(context.ExpressionExecutionContext);
        context.WaitForEvent(eventName, EventReceivedAsync);
        return default;
    }

    protected async ValueTask EventReceivedAsync(StepExecutionContext context)
    {
        var input = (TResult?)context.GetEventInput();
        Result.Set(context, input);
        await OnEventReceivedAsync(context, input).ConfigureAwait(false);
        await context.CompleteStepAsync().ConfigureAwait(false);
    }
}