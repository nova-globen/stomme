namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides contextual information about a signal.
/// </summary>
public class SignalContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SignalContext"/> class.
    /// </summary>
    public SignalContext(StepExecutionContext receiverStepExecutionContext, StepExecutionContext senderStepExecutionContext, CancellationToken cancellationToken)
    {
        ReceiverStepExecutionContext = receiverStepExecutionContext;
        SenderStepExecutionContext = senderStepExecutionContext;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// The <see cref="StepExecutionContext"/> receiving the signal.
    /// </summary>
    public StepExecutionContext ReceiverStepExecutionContext { get; init; }
    
    /// <summary>
    /// The <see cref="StepExecutionContext"/> sending the signal.
    /// </summary>
    public StepExecutionContext SenderStepExecutionContext { get; init; }
    
    /// <summary>
    /// Returns true if the receiver is the same as the sender.
    /// </summary>
    public bool IsSelf => SenderStepExecutionContext.Step == ReceiverStepExecutionContext.Step;
    
    /// <summary>
    /// The cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
    
    internal bool StopPropagationRequested { get; private set; }

    /// <summary>
    /// Stops the signal from propagating further up the step execution context hierarchy.
    /// </summary>
    public void StopPropagation() => StopPropagationRequested = true;
}