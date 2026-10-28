using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;

public static class StepExecutionContextExtensions
{
    private const string GraphTransientProperty = "FlowGraph";

    extension(Steps.Flowchart flowchart)
    {
        public IStep? GetStartStep(string? triggerStepId)
        {
            return flowchart.GetTriggerStep(triggerStepId)
                   ?? flowchart.Start
                   ?? flowchart.GetExplicitStartStep()
                   ?? flowchart.GetCanStartPipelineStep()
                   ?? flowchart.GetRootStep()
                   ?? flowchart.Steps.FirstOrDefault(x => x is not IStartsOnlyWhenNamed);
        }

        /// <summary>
        /// Gets the step that was triggered by the specified trigger step ID.
        /// </summary>
        public IStep? GetTriggerStep(string? triggerStepId)
        {
            return triggerStepId == null 
                ? null : 
                flowchart.Steps.FirstOrDefault(x => x.Id == triggerStepId);
        }

        /// <summary>
        /// Gets the explicit Start step from the flowchart.
        /// </summary>
        public IStep? GetExplicitStartStep()
        {
            return flowchart.Steps.FirstOrDefault(x => x is Start);
        }

        /// <summary>
        /// Gets the first step marked as "Can Start Pipeline".
        /// </summary>
        public IStep? GetCanStartPipelineStep()
        {
            return flowchart.Steps.FirstOrDefault(x => x.GetCanStartPipeline() && x is not IStartsOnlyWhenNamed);
        }
    }

    extension(StepExecutionContext context)
    {
        /// <summary>
        /// Checks if there is any pending work for the flowchart.
        /// </summary>
        public bool HasPendingWork()
        {
            return context.HasRunningStepInstances()
                   || context.HasScheduledWork()
                   || context.HasUnconsumedTokens()
                   || context.HasFaultedChildren();
        }

        public FlowGraph GetFlowGraph()
        {
            // Store in TransientProperties so FlowChart is not persisted in PipelineState
            var flowchart = (Steps.Flowchart)context.Step;
            var startStep = flowchart.GetStartStep(context.PipelineExecutionContext.TriggerStepId);
            return context.TransientProperties.GetOrAdd(GraphTransientProperty, () => new FlowGraph(flowchart.Connections, startStep));
        }

        public async Task CancelInboundAncestorsAsync(IStep step)
        {
            if(context.Step is not Steps.Flowchart)
                throw new InvalidOperationException("Step context is not a flowchart.");
        
            var flowGraph = context.GetFlowGraph();
            var ancestorSteps = flowGraph.GetAncestorSteps(step);
            var inboundStepExecutionContexts = context.PipelineExecutionContext.StepExecutionContexts.Where(x => ancestorSteps.Contains(x.Step) && x.ParentStepExecutionContext == context).ToList();

            // Cancel each ancestor step.
            foreach (var stepExecutionContext in inboundStepExecutionContexts)
            {
                await stepExecutionContext.CancelStepAsync().ConfigureAwait(false);
            }
        }
        
        /// <summary>
        /// Checks if the flowchart has any unconsumed tokens.
        /// </summary>
        public bool HasUnconsumedTokens()
        {
            var flowchart = (Steps.Flowchart)context.Step;
            return flowchart.GetTokenList(context).Any(x => x is { Consumed: false, Blocked: false });
        }

        /// <summary>
        /// Checks if the flowchart has any running step instances.
        /// </summary>
        public bool HasRunningStepInstances()
        {
            var flowchart = (Steps.Flowchart)context.Step;
            var stepIds = flowchart.Steps.Select(x => x.Id).ToList();
            var children = context.Children;
            return children.Where(x => stepIds.Contains(x.Step.Id)).Any(x => x.Status == StepStatus.Running);
        }

        /// <summary>
        /// Checks if the flowchart has any scheduled work items.
        /// </summary>
        public bool HasScheduledWork()
        {
            var pipelineExecutionContext = context.PipelineExecutionContext;

            return pipelineExecutionContext.Scheduler.List().Any(workItem =>
            {
                var ownerInstanceId = workItem.Owner?.Id;

                if (ownerInstanceId == null)
                    return false;

                if (ownerInstanceId == context.Id)
                    return true;

                var ownerContext = context.PipelineExecutionContext.StepExecutionContexts.First(x => x.Id == ownerInstanceId);
                var ancestors = ownerContext.GetAncestors().ToList();

                return ancestors.Any(x => x == context);
            });
        }

        public bool HasFaultedChildren()
        {
            return context.Children.Any(x => x.Status == StepStatus.Faulted);
        }
    }
}