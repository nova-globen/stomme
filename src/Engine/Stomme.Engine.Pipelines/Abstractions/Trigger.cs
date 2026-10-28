namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a step that acts as a pipeline trigger.
/// </summary>
public abstract class Trigger : Step, ITrigger
{
    /// <inheritdoc />
    protected Trigger(string? source = null, int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    protected Trigger(string stepType, int version = 1, string? source = null, int? line = null) : base(stepType, version, source, line)
    {
    }

    ValueTask<IEnumerable<object>> ITrigger.GetTriggerPayloadsAsync(TriggerIndexingContext context) => GetTriggerPayloadsAsync(context);

    /// <summary>
    /// Override this method to return trigger data.  
    /// </summary>
    protected virtual ValueTask<IEnumerable<object>> GetTriggerPayloadsAsync(TriggerIndexingContext context)
    {
        var hashes = GetTriggerPayloads(context);
        return ValueTask.FromResult(hashes);
    }

    /// <summary>
    /// Override this method to return trigger data.
    /// </summary>
    protected virtual IEnumerable<object> GetTriggerPayloads(TriggerIndexingContext context) => [GetTriggerPayload(context)];

    /// <summary>
    /// Override this method to return a trigger datum.
    /// </summary>
    protected virtual object GetTriggerPayload(TriggerIndexingContext context) => new();
}

public abstract class Trigger<TResult> : Step<TResult>, ITrigger
{
    protected Trigger(string? source = null, int? line = null) : base(source, line)
    {
    }

    protected Trigger(string stepType, int version = 1, string? source = null, int? line = null) : base(stepType, version, source, line)
    {
    }

    ValueTask<IEnumerable<object>> ITrigger.GetTriggerPayloadsAsync(TriggerIndexingContext context) => GetTriggerPayloadsAsync(context);

    /// <summary>
    /// Override this method to return trigger data.  
    /// </summary>
    protected virtual ValueTask<IEnumerable<object>> GetTriggerPayloadsAsync(TriggerIndexingContext context)
    {
        var hashes = GetTriggerPayloads(context);
        return ValueTask.FromResult(hashes);
    }

    /// <summary>
    /// Override this method to return a trigger payload.
    /// </summary>
    protected virtual IEnumerable<object> GetTriggerPayloads(TriggerIndexingContext context) => [GetTriggerPayload(context)];

    /// <summary>
    /// Override this method to return a trigger payload.
    /// </summary>
    protected virtual object GetTriggerPayload(TriggerIndexingContext context) => new();
}