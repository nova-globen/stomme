using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Signals;

namespace Stomme.Engine.Pipelines.Behaviors;

/// <summary>
/// Implements a "break" behavior that handles the <see cref="BreakSignal"/> signal.
/// Stops propagation of the signal, which is useful for looping steps such as <see cref="While"/>, <see cref="For"/> ans <see cref="ForEach"/>.
/// </summary>
public class BreakBehavior : Behavior
{
    /// <summary>
    /// Constructor.
    /// </summary>
    public BreakBehavior(IStep owner) : base(owner)
    {
        OnSignalReceived<BreakSignal>(OnBreak);
        OnSignalReceived<CompleteCompositeSignal>(OnCompleteCompositeAsync);
    }

    private async ValueTask OnCompleteCompositeAsync(CompleteCompositeSignal signal, SignalContext context)
    {
        // Cancel each descendant to clear bookmarks and cancel jobs etc.
        await CancelDescendantsAsync(context).ConfigureAwait(false);

        // Mark this step as completed.
        await context.ReceiverStepExecutionContext.CompleteStepAsync().ConfigureAwait(false);
    }

    private void OnBreak(BreakSignal signal, SignalContext context)
    {
        // Prevent bubbling.
        context.StopPropagation();
        
        // Set the IsBreaking property to true.
        context.ReceiverStepExecutionContext.SetProperty("IsBreaking", true);
    }
    
    private async Task CancelDescendantsAsync(SignalContext context)
    {
        await context.ReceiverStepExecutionContext.CancelStepAsync().ConfigureAwait(false);
    }
}