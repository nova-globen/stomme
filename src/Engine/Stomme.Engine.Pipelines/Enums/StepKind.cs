namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents the kind of a step, which affects how it is executed.
/// </summary>
public enum StepKind
{
    /// <summary>
    /// Always run synchronously.
    /// </summary>
    Action,
    
    /// <summary>
    /// Can be used to trigger new pipelines.
    /// </summary>
    Trigger,
    
    /// <summary>
    /// Always run asynchronously.
    /// </summary>
    Job,
    
    /// <summary>
    /// Run synchronously by default (like <see cref="Action"/>, but can be configured to run asynchronously (like <see cref="Job"/>).
    /// </summary>
    Task
}