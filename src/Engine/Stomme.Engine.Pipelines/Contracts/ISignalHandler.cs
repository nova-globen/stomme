namespace Stomme.Engine.Pipelines;

/// <summary>
/// Handles signals.
/// </summary>
public interface ISignalHandler
{
    /// <summary>
    /// Receives a signal.
    /// </summary>
    ValueTask ReceiveSignalAsync(object signal, SignalContext context);
}