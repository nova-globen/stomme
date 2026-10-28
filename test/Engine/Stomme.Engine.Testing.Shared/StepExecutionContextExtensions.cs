using Stomme.Engine.Pipelines;

namespace Stomme.Engine.Testing.Shared;

/// <summary>
/// Provides extension methods for <see cref="StepExecutionContext"/> to enhance its functionality.
/// </summary>
public static class StepExecutionContextExtensions
{
    /// <param name="stepExecutionContext">The step execution context instance used for checking the scheduled step.</param>
    extension(StepExecutionContext stepExecutionContext)
    {
        /// <summary>
        /// Determines whether the specified step is scheduled in the pipeline execution context.
        /// </summary>
        /// <param name="step">The step to check for scheduling.</param>
        /// <returns>True if the specified step is scheduled; otherwise, false.</returns>
        public bool HasScheduledStep(IStep step)
        {
            return stepExecutionContext.PipelineExecutionContext.Scheduler.Find(x => x.Step == step) != null;
        }

        /// <summary>
        /// Retrieves the collection of outcomes recorded in the execution context's journal data.
        /// </summary>
        /// <returns>A collection of outcome strings, or an empty collection if no outcomes are present.</returns>
        public IEnumerable<string> GetOutcomes()
        {
            return stepExecutionContext.JournalData.TryGetValue("Outcomes", out var outcomes) && outcomes is string[] arr ? arr : [];
        }

        /// <summary>
        /// Determines whether the specified outcome exists in the current step execution context.
        /// </summary>
        /// <param name="outcome">The outcome to verify within the execution context.</param>
        /// <returns>True if the specified outcome exists; otherwise, false.</returns>
        public bool HasOutcome(string outcome)
        {
            return stepExecutionContext.GetOutcomes().Contains(outcome);
        }
    }
}