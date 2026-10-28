using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Adds extension methods to <see cref="StepExecutionContext"/>.
/// </summary>
public static class PipelineExecutionContextExtensions
{
    extension(PipelineExecutionContext pipelineExecutionContext)
    {
        /// <summary>
        /// Schedules the pipeline for execution.
        /// </summary>
        public StepWorkItem SchedulePipeline(
            IDictionary<string, object>? input = null,
            IEnumerable<Variable>? variables = null,
            string? schedulingStepExecutionId = null,
            string? schedulingPipelineInstanceId = null,
            int? schedulingCallStackDepth = null)
        {
            var pipeline = pipelineExecutionContext.Pipeline;
            var workItem = new StepWorkItem(
                pipeline,
                input: input,
                variables: variables,
                schedulingStepExecutionId: schedulingStepExecutionId,
                schedulingPipelineInstanceId: schedulingPipelineInstanceId,
                schedulingCallStackDepth: schedulingCallStackDepth);
            pipelineExecutionContext.Scheduler.Schedule(workItem);
            return workItem;
        }

        /// <summary>
        /// Schedules the root step of the pipeline.
        /// </summary>
        public StepWorkItem ScheduleRoot(IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
        {
            var pipeline = pipelineExecutionContext.Pipeline;
            var workItem = new StepWorkItem(pipeline.Root, input: input, variables: variables);
            pipelineExecutionContext.Scheduler.Schedule(workItem);
            return workItem;
        }

        /// <summary>
        /// Schedules the specified step of the pipeline.
        /// </summary>
        public StepWorkItem ScheduleStep(IStep step, IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
        {
            var workItem = new StepWorkItem(step, input: input, variables: variables);
            pipelineExecutionContext.Scheduler.Schedule(workItem);
            return workItem;
        }

        /// <summary>
        /// Schedules the specified step execution context of the pipeline.
        /// </summary>
        public StepWorkItem ScheduleStepExecutionContext(StepExecutionContext stepExecutionContext, IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
        {
            var workItem = new StepWorkItem(
                stepExecutionContext.Step,
                input: input,
                variables: variables,
                existingStepExecutionContext: stepExecutionContext);
            pipelineExecutionContext.Scheduler.Schedule(workItem);
            return workItem;
        }

        /// <summary>
        /// Schedules the step of the specified bookmark.
        /// </summary>
        /// <returns>The created work item, or <c>null</c> if the specified bookmark doesn't exist in the <see cref="PipelineExecutionContext"/></returns> 
        public StepWorkItem? ScheduleBookmark(Bookmark bookmark, IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
        {
            // Get the step execution context that owns the bookmark.
            var bookmarkedStepContext = pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == bookmark.StepInstanceId);
            var logger = pipelineExecutionContext.GetRequiredService<ILogger<PipelineExecutionContext>>();

            if (bookmarkedStepContext == null)
            {
                logger.LogWarning("Could not find step execution context with ID {StepInstanceId} for bookmark {BookmarkId}", bookmark.StepInstanceId, bookmark.Id);
                return null;
            }

            var bookmarkedStep = bookmarkedStepContext.Step;

            // Schedule the step to resume.
            var workItem = new StepWorkItem(bookmarkedStep)
            {
                ExistingStepExecutionContext = bookmarkedStepContext,
                Input = input ?? new Dictionary<string, object>(),
                Variables = variables
            };
            pipelineExecutionContext.Scheduler.Schedule(workItem);

            // If no resumption point was specified, use a "noop" to prevent the regular "ExecuteAsync" method to be invoked and instead complete the step.
            // Unless the bookmark is configured to auto-complete, in which case we'll just complete the step.
            pipelineExecutionContext.ExecuteDelegate = bookmark.CallbackMethodName != null
                ? bookmarkedStep.GetResumeStepDelegate(bookmark.CallbackMethodName)
                : bookmark.AutoComplete
                    ? PipelineExecutionContext.Complete
                    : PipelineExecutionContext.Noop;

            // Store the bookmark to resume in the context.
            pipelineExecutionContext.ResumedBookmarkContext = new(bookmark);
            logger.LogDebug("Scheduled step {StepId} to resume from bookmark {BookmarkId}", bookmarkedStep.Id, bookmark.Id);

            return workItem;
        }

        /// <summary>
        /// Schedules the specified step.
        /// </summary>
        public StepWorkItem Schedule(StepNode stepNode,
            StepExecutionContext owner,
            ScheduleWorkOptions? options = null)
        {
            var schedulerStrategy = pipelineExecutionContext.GetRequiredService<IPipelineExecutionContextSchedulerStrategy>();
            return schedulerStrategy.Schedule(pipelineExecutionContext, stepNode, owner, options);
        }

        /// <summary>
        /// Returns true if all steps have completed or canceled, false otherwise.
        /// </summary>
        public bool AllStepsCompleted() => pipelineExecutionContext.StepExecutionContexts.All(x => x.IsCompleted);

        public object? GetOutputByStepId(string stepId, string? outputName = null)
        {
            var outputRegister = pipelineExecutionContext.GetStepOutputRegister();
            return outputRegister.FindOutputByStepId(stepId, outputName);
        }

        public IEnumerable<StepExecutionContext> FindStepExecutionContexts(StepHandle stepHandle)
        {
            if (stepHandle.StepInstanceId != null)
                return pipelineExecutionContext.StepExecutionContexts.Where(x => x.Id == stepHandle.StepInstanceId);
            if (stepHandle.StepNodeId != null)
                return pipelineExecutionContext.StepExecutionContexts.Where(x => x.NodeId == stepHandle.StepNodeId);
            if (stepHandle.StepId != null)
                return pipelineExecutionContext.StepExecutionContexts.Where(x => x.Step.Id == stepHandle.StepId);
            if (stepHandle.StepHash != null)
            {
                var step = pipelineExecutionContext.FindStepByHash(stepHandle.StepHash);
                return step != null ? pipelineExecutionContext.StepExecutionContexts.Where(x => x.Step.NodeId == step.NodeId) : [];
            }

            return [];
        }
    }
}