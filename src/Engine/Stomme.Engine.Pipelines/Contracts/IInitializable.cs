namespace Stomme.Engine.Pipelines;

/// <summary>
/// When implemented by a step, it is invoked when the graph is built.
/// </summary>
public interface IInitializable : IStep
{
    /// <summary>
    /// Called by the system to initialize the step.
    /// </summary>
    ValueTask InitializeAsync(InitializationContext context);
}

/// <summary>
/// Provides access to contextual services and information.
/// </summary>
public record InitializationContext(IServiceProvider ServiceProvider, CancellationToken CancellationToken);