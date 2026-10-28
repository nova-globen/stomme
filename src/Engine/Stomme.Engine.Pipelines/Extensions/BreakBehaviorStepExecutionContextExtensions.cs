namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides extension methods for <see cref="StepExecutionContext"/>. 
/// </summary>
public static class BreakBehaviorStepExecutionContextExtensions
{
    /// <param name="context"></param>
    extension(StepExecutionContext context)
    {
        /// <summary>
        /// Gets a value indicating whether the current step is breaking out of a loop.
        /// </summary>
        public bool GetIsBreaking() => context.GetProperty<bool>("IsBreaking");

        /// <summary>
        /// Sets a value indicating whether the current step is breaking out of a loop.
        /// </summary>
        public void SetIsBreaking() => context.SetProperty<bool>("IsBreaking", true);
    }
}