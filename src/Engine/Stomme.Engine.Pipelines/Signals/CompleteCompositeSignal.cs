namespace Stomme.Engine.Pipelines.Signals;

/// <summary>
/// Sent by child steps to notify their composite container that it should complete.
/// </summary>
public record CompleteCompositeSignal(object? Value = default);