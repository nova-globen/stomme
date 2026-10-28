using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Adds extension methods to <see cref="StepExecutionContext"/>.
/// </summary>
public static class BackgroundStepExecutionContextExtensions
{
    /// <summary>
    /// A key into the step execution context's transient properties that indicates whether the current step is being executed in the background.
    /// </summary>
    public static readonly object IsBackgroundExecution = new();

    /// <param name="stepExecutionContext"></param>
    extension(StepExecutionContext stepExecutionContext)
    {
        /// <summary>
        /// Configures the step execution context to execute the current step in the background.
        /// </summary>
        public void SetIsBackgroundExecution(bool value = true)
        {
            stepExecutionContext.TransientProperties[IsBackgroundExecution] = value;
        }

        /// <summary>
        /// Gets a value indicating whether the current step is being executed in the background.
        /// </summary>
        public bool GetIsBackgroundExecution()
        {
            return stepExecutionContext.TransientProperties.ContainsKey(IsBackgroundExecution);
        }

        /// <summary>
        /// Sets the background outcomes.
        /// </summary>
        public void SetBackgroundOutcomes(IEnumerable<string> outcomes)
        {
            var outcomesList = outcomes.ToList();
            stepExecutionContext.SetProperty("BackgroundOutcomes", outcomesList);
        }

        /// <summary>
        /// Gets the background outcomes.
        /// </summary>
        public IEnumerable<string>? GetBackgroundOutcomes()
        {
            return stepExecutionContext.GetProperty<IEnumerable<string>>("BackgroundOutcomes");
        }

        /// <summary>
        /// Sets the background outcomes.
        /// </summary>
        public void SetBackgroundCompletion()
        {
            stepExecutionContext.SetProperty("BackgroundCompletion", true);
        }

        /// <summary>
        /// Gets the background outcomes.
        /// </summary>
        public bool? GetBackgroundCompleted()
        {
            return stepExecutionContext.GetProperty<bool>("BackgroundCompletion");
        }

        /// <summary>
        /// Sets the background scheduled steps.
        /// </summary>
        public void SetBackgroundScheduledSteps(IEnumerable<ScheduledStep> scheduledSteps)
        {
            var scheduledStepsList = scheduledSteps.ToList();
            stepExecutionContext.SetProperty("BackgroundScheduledSteps", scheduledStepsList);
        }

        /// <summary>
        /// Gets the background scheduled steps.
        /// </summary>
        public IEnumerable<ScheduledStep> GetBackgroundScheduledSteps()
        {
            return stepExecutionContext.GetProperty<IEnumerable<ScheduledStep>>("BackgroundScheduledSteps") ?? [];
        }
    }
}