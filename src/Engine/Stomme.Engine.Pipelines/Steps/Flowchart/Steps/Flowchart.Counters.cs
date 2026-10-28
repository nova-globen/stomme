using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Contracts;
using Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Signals;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Steps;

public partial class Flowchart
{
    private const string ScopeProperty = "FlowScope";
    private const string GraphTransientProperty = "FlowGraph";
    private const string BackwardConnectionStepInput = "BackwardConnection";
    
    
    private async ValueTask OnChildCompletedCounterBasedLogicAsync(StepCompletedContext context)
    {
        var flowchartContext = context.TargetContext;
        var completedStepContext = context.ChildContext;
        var completedStep = completedStepContext.Step;
        var result = context.Result;

        // Determine the outcomes from the completed step
        var outcomes = result is Outcomes o ? o : Outcomes.Default;

        await ProcessChildCompletedAsync(flowchartContext, completedStep, completedStepContext, outcomes).ConfigureAwait(false);
    }

    private IStep? GetStartStep(StepExecutionContext context)
    {
        // If there's a trigger that triggered this pipeline, use that.
        var triggerStepId = context.PipelineExecutionContext.TriggerStepId;
        var triggerStep = triggerStepId != null ? Steps.FirstOrDefault(x => x.Id == triggerStepId) : null;

        if (triggerStep != null)
            return triggerStep;

        // If an explicit Start step was provided, use that.
        if (Start != null)
            return Start;

        // If there is a Start step on the flowchart, use that.
        var startStep = Steps.FirstOrDefault(x => x is Start);

        if (startStep != null)
            return startStep;

        // If there's a step marked as "Can Start Pipeline", use that.
        var canStartPipelineStep = Steps.FirstOrDefault(x => x.GetCanStartPipeline() && x is not IStartsOnlyWhenNamed);

        if (canStartPipelineStep != null)
            return canStartPipelineStep;

        // If there is a single step that has no inbound connections, use that.
        var root = GetRootStep();

        if (root != null)
            return root;

        // If no start step found, return the first step - never one that starts a run only when named.
        return Steps.FirstOrDefault(x => x is not IStartsOnlyWhenNamed);
    }

    /// <summary>
    /// Checks if there is any pending work for the flowchart.
    /// </summary>
    private bool HasPendingWork(StepExecutionContext context)
    {
        var pipelineExecutionContext = context.PipelineExecutionContext;

        // Use HashSet for O(1) lookups
        var stepIds = new HashSet<string>(Steps.Select(x => x.Id));

        // Short circuit evaluation - check running instances first before more expensive scheduler check
        if (context.Children.Any(x => stepIds.Contains(x.Step.Id) && x.Status == StepStatus.Running))
            return true;

        // Scheduler check - optimize to avoid repeated LINQ evaluations
        var scheduledItems = pipelineExecutionContext.Scheduler.List().ToList();

        return scheduledItems.Any(workItem =>
        {
            var ownerInstanceId = workItem.Owner?.Id;

            if (ownerInstanceId == null)
                return false;

            if (ownerInstanceId == context.Id)
                return true;

            var ownerContext = pipelineExecutionContext.StepExecutionContexts.First(x => x.Id == ownerInstanceId);
            return ownerContext.GetAncestors().Any(x => x == context);
        });
    }

    private IStep? GetRootStep()
    {
        // Get the first step that has no inbound connections.
        var query =
            from step in Steps
            let inboundConnections = Connections.Any(x => x.Target.Step == step)
            where !inboundConnections && step is not IStartsOnlyWhenNamed
            select step;

        var rootStep = query.FirstOrDefault();
        return rootStep;
    }

    private FlowGraph GetFlowGraph(StepExecutionContext context)
    {
        // Store in TransientProperties so FlowChart is not persisted in PipelineState 
        return context.TransientProperties.GetOrAdd(GraphTransientProperty, () => new FlowGraph(Connections, GetStartStep(context)));
    }

    private FlowScope GetFlowScope(StepExecutionContext context)
    {
        return context.GetProperty(ScopeProperty, () => new FlowScope());
    }

    private async ValueTask ProcessChildCompletedAsync(StepExecutionContext flowchartContext, IStep completedStep, StepExecutionContext completedStepContext, Outcomes outcomes)
    {
        if (flowchartContext.Step != this)
        {
            throw new("Target context step must be this flowchart");
        }

        // If the completed step's status is anything but "Completed", do not schedule its outbound steps.
        if (completedStepContext.Status != StepStatus.Completed)
        {
            return;
        }

        // If the complete step is a terminal node, complete the flowchart immediately.
        if (completedStep is ITerminalNode)
        {
            await flowchartContext.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        // Schedule the outbound steps
        var flowGraph = GetFlowGraph(flowchartContext);
        var flowScope = GetFlowScope(flowchartContext);
        var completedStepExcecutedByBackwardConnection = completedStepContext.StepInput.GetValueOrDefault<bool>(BackwardConnectionStepInput);
        bool hasScheduledStep = await MaybeScheduleOutboundStepsAsync(flowGraph, flowScope, flowchartContext, completedStep, completedStepContext, outcomes, OnChildCompletedAsync, completedStepExcecutedByBackwardConnection).ConfigureAwait(false);

        // If there are not any outbound connections, complete the flowchart step if there is no other pending work
        if (!hasScheduledStep)
        {
            await CompleteIfNoPendingWorkAsync(flowchartContext).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Schedules outbound steps based on the flowchart's structure and execution state.
    /// This method determines whether a step should be scheduled based on visited connections,
    /// forward traversal rules, and backward connections. If outcomes is Outcomes.Empty, it indicates 
    /// that the step should be skipped - all outbound connections will be visited and treated as 
    /// not followed.
    /// </summary>
    /// <param name="flowGraph">The graph representation of the flowchart.</param>
    /// <param name="flowScope">Tracks step and connection visits.</param>
    /// <param name="flowchartContext">The execution context of the flowchart.</param>
    /// <param name="step">The current step being processed.</param>
    /// <param name="completedStepContext">The execution context of the completed step (for call stack tracking).</param>
    /// <param name="outcomes">The outcomes that determine which connections were followed.</param>
    /// <param name="completionCallback">The callback to invoke upon step completion.</param>
    /// <param name="completedStepExecutedByBackwardConnection">Indicates if the completed step
    /// was executed due to a backward connection.</param>
    /// <returns>True if at least one step was scheduled; otherwise, false.</returns>
    private static async ValueTask<bool> MaybeScheduleOutboundStepsAsync(FlowGraph flowGraph, FlowScope flowScope, StepExecutionContext flowchartContext, IStep step, StepExecutionContext? completedStepContext, Outcomes outcomes, StepCompletionCallback completionCallback, bool completedStepExecutedByBackwardConnection = false)
    {
        bool hasScheduledStep = false;

        // Check if the step is dangling (i.e., it is not reachable from the flowchart graph)
        if (flowGraph.IsDanglingStep(step))
        {
            throw new($"Step {step.Id} is not reachable from the flowchart graph. Unable to schedule it's outbound steps.");
        }

        // Register the step as visited unless it was executed due to a backward connection
        if (!completedStepExecutedByBackwardConnection)
        {
            flowScope.RegisterStepVisit(step);
        }

        // Process each outbound connection from the current step
        foreach (var outboundConnection in flowGraph.GetOutboundConnections(step))
        {
            var connectionFollowed = outcomes.Names.Contains(outboundConnection.Source.Port);
            flowScope.RegisterConnectionVisit(outboundConnection, connectionFollowed);
            var outboundStep = outboundConnection.Target.Step;

            // Determine the scheduling strategy based on connection-type.
            if (flowGraph.IsBackwardConnection(outboundConnection, out var backwardConnectionIsValid))
                // Backward connections are scheduled differently
                hasScheduledStep |= await MaybeScheduleBackwardConnectionStepAsync(flowGraph, flowchartContext, completedStepContext, outboundConnection, outboundStep, connectionFollowed, backwardConnectionIsValid, completionCallback).ConfigureAwait(false);
            else
                hasScheduledStep |= await MaybeScheduleOutboundStepAsync(flowGraph, flowScope, flowchartContext, completedStepContext, outboundConnection, outboundStep, completionCallback).ConfigureAwait(false);
        }

        return hasScheduledStep;
    }

    /// <summary>
    /// Schedules an outbound step that originates from a backward connection.
    /// </summary>
    private static async ValueTask<bool> MaybeScheduleBackwardConnectionStepAsync(FlowGraph flowGraph, StepExecutionContext flowchartContext, StepExecutionContext? completedStepContext, Connection outboundConnection, IStep outboundStep, bool connectionFollowed, bool backwardConnectionIsValid, StepCompletionCallback completionCallback)
    {
        if (!connectionFollowed)
        {
            return false;
        }

        if (!backwardConnectionIsValid)
        {
            throw new($"Invalid backward connection: Every path from the source ('{outboundConnection.Source.Step.Id}') must go through the target ('{outboundConnection.Target.Step.Id}') when tracing back to the start.");
        }

        var scheduleWorkOptions = new ScheduleWorkOptions
        {
            CompletionCallback = completionCallback,
            Input = new Dictionary<string, object>() { { BackwardConnectionStepInput, true } },
            SchedulingStepExecutionId = completedStepContext?.Id
        };

        await flowchartContext.ScheduleStepAsync(outboundStep, scheduleWorkOptions).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Determines the merge mode for a given outbound step. If the outbound step is a FlowJoin, it retrieves its configured 
    /// mode. Otherwise, it defaults to FlowJoinMode.WaitAllActive for implicit joins.
    /// </summary>
    private static async ValueTask<FlowJoinMode> GetMergeModeAsync(StepExecutionContext flowchartContext, IStep outboundStep)
    {
        if (outboundStep is FlowJoin)
        {
            var outboundStepExecutionContext = await flowchartContext.PipelineExecutionContext.CreateStepExecutionContextAsync(outboundStep).ConfigureAwait(false);
            return await outboundStepExecutionContext.EvaluateInputPropertyAsync<FlowJoin, FlowJoinMode>(x => x.Mode).ConfigureAwait(false);
        }
        else
        {
            // Implicit join case - treat as WaitAllActive
            return FlowJoinMode.WaitAllActive;
        }
    }

    /// <summary>
    /// Schedules a join step based on inbound connection statuses.
    /// </summary>
    private static async ValueTask<bool> MaybeScheduleOutboundStepAsync(FlowGraph flowGraph, FlowScope flowScope, StepExecutionContext flowchartContext, StepExecutionContext? completedStepContext, Connection outboundConnection, IStep outboundStep, StepCompletionCallback completionCallback)
    {
        FlowJoinMode mode = await GetMergeModeAsync(flowchartContext, outboundStep).ConfigureAwait(false);

        return mode switch
        {
            FlowJoinMode.WaitAll => await MaybeScheduleWaitAllStepAsync(flowGraph, flowScope, flowchartContext, completedStepContext, outboundStep, completionCallback).ConfigureAwait(false),
            FlowJoinMode.WaitAllActive => await MaybeScheduleWaitAllActiveStepAsync(flowGraph, flowScope, flowchartContext, completedStepContext, outboundStep, completionCallback).ConfigureAwait(false),
            FlowJoinMode.WaitAny => await MaybeScheduleWaitAnyStepAsync(flowGraph, flowScope, flowchartContext, completedStepContext, outboundConnection, outboundStep, completionCallback).ConfigureAwait(false),
            _ => throw new($"Unsupported FlowJoinMode: {mode}"),
        };
    }

    /// <summary>
    /// Determines whether to schedule a step based on the FlowJoinMode.WaitAll behavior.
    /// If all inbound connections were visited, it checks if they were all followed to decide whether to schedule or skip the step.
    /// </summary>
    private static async ValueTask<bool> MaybeScheduleWaitAllStepAsync(FlowGraph flowGraph, FlowScope flowScope, StepExecutionContext flowchartContext, StepExecutionContext? completedStepContext, IStep outboundStep, StepCompletionCallback completionCallback)
    {
        if (!flowScope.AllInboundConnectionsVisited(flowGraph, outboundStep))
            // Not all inbound connections have been visited yet; do not schedule anything yet.
            return false;

        if (flowScope.AllInboundConnectionsFollowed(flowGraph, outboundStep))
            // All inbound connections were followed; schedule the outbound step.
            return await ScheduleOutboundStepAsync(flowchartContext, completedStepContext, outboundStep, completionCallback).ConfigureAwait(false);
        else
            // No inbound connections were followed; skip the outbound step.
            return await SkipOutboundStepAsync(flowGraph, flowScope, flowchartContext, completedStepContext, outboundStep, completionCallback).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether to schedule a step based on the FlowJoinMode.WaitAllActive behavior.
    /// If all inbound connections have been visited, it checks if any were followed to decide whether to schedule or skip the step.
    /// </summary>
    private static async ValueTask<bool> MaybeScheduleWaitAllActiveStepAsync(FlowGraph flowGraph, FlowScope flowScope, StepExecutionContext flowchartContext, StepExecutionContext? completedStepContext, IStep outboundStep, StepCompletionCallback completionCallback)
    {
        if (!flowScope.AllInboundConnectionsVisited(flowGraph, outboundStep))
            // Not all inbound connections have been visited yet; do not schedule anything yet.
            return false;

        if (flowScope.AnyInboundConnectionsFollowed(flowGraph, outboundStep))
            // At least one inbound connection was followed; schedule the outbound step.
            return await ScheduleOutboundStepAsync(flowchartContext, completedStepContext, outboundStep, completionCallback).ConfigureAwait(false);
        else
            // No inbound connections were followed; skip the outbound step.
            return await SkipOutboundStepAsync(flowGraph, flowScope, flowchartContext, completedStepContext, outboundStep, completionCallback).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether to schedule a step based on the FlowJoinMode.WaitAny behavior.
    /// If any inbound connection has been followed, it schedules the step and cancels remaining inbound steps.
    /// If a subsequent inbound connection is followed after the step has been scheduled, it ignores it.
    /// </summary>
    private static async ValueTask<bool> MaybeScheduleWaitAnyStepAsync(FlowGraph flowGraph, FlowScope flowScope, StepExecutionContext flowchartContext, StepExecutionContext? completedStepContext, Connection outboundConnection, IStep outboundStep, StepCompletionCallback completionCallback)
    {
        if (flowScope.ShouldIgnoreConnection(outboundConnection, outboundStep))
            // Ignore the connection if the outbound step has already completed (JoinAny scenario)
            return false;

        if (flowchartContext.PipelineExecutionContext.Scheduler.List().Any(workItem => workItem.Owner == flowchartContext && workItem.Step == outboundStep))
            // Ignore the connection if the outbound step is already scheduled
            return false;

        if (flowScope.AnyInboundConnectionsFollowed(flowGraph, outboundStep))
        {
            // This is the first inbound connection followed; schedule the outbound step
            var scheduleResponse = await ScheduleOutboundStepAsync(flowchartContext, completedStepContext, outboundStep, completionCallback).ConfigureAwait(false);
            // An inbound connection has been followed; cancel remaining inbound steps
            await CancelRemainingInboundStepsAsync(flowchartContext, outboundStep).ConfigureAwait(false);

            return scheduleResponse;
        }

        if (flowScope.AllInboundConnectionsVisited(flowGraph, outboundStep))
            // All inbound connections have been visited without any being followed; skip the outbound step
            return await SkipOutboundStepAsync(flowGraph, flowScope, flowchartContext, completedStepContext, outboundStep, completionCallback).ConfigureAwait(false);

        // No inbound connections have been followed yet; do not schedule anything yet.
        return false;
    }

    /// <summary>
    /// Schedules the outbound step.
    /// </summary>
    private static async ValueTask<bool> ScheduleOutboundStepAsync(StepExecutionContext flowchartContext, StepExecutionContext? completedStepContext, IStep outboundStep, StepCompletionCallback completionCallback)
    {
        var options = new ScheduleWorkOptions
        {
            CompletionCallback = completionCallback,
            SchedulingStepExecutionId = completedStepContext?.Id
        };
        await flowchartContext.ScheduleStepAsync(outboundStep, options).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Skips the outbound step by propagating skipped connections.
    /// </summary>
    private static async ValueTask<bool> SkipOutboundStepAsync(FlowGraph flowGraph, FlowScope flowScope, StepExecutionContext flowchartContext, StepExecutionContext? completedStepContext, IStep outboundStep, StepCompletionCallback completionCallback)
    {
        return await MaybeScheduleOutboundStepsAsync(flowGraph, flowScope, flowchartContext, outboundStep, completedStepContext, Outcomes.Empty, completionCallback).ConfigureAwait(false);
    }

    private static async ValueTask CancelRemainingInboundStepsAsync(StepExecutionContext flowchartContext, IStep outboundStep)
    {
        var flowchart = (Flowchart)flowchartContext.Step;
        var flowGraph = flowchart.GetFlowGraph(flowchartContext);
        var ancestorSteps = flowGraph.GetAncestorSteps(outboundStep);
        var inboundStepExecutionContexts = flowchartContext.PipelineExecutionContext.StepExecutionContexts.Where(x => ancestorSteps.Contains(x.Step) && x.ParentStepExecutionContext == flowchartContext).ToList();

        // Cancel each ancestor step.
        foreach (var stepExecutionContext in inboundStepExecutionContexts)
        {
            await stepExecutionContext.CancelStepAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask OnScheduleOutcomesAsync(ScheduleStepOutcomes signal, SignalContext context)
    {
        var flowchartContext = context.ReceiverStepExecutionContext;
        var schedulingStepContext = context.SenderStepExecutionContext;
        var schedulingStep = schedulingStepContext.Step;
        var outcomes = new Outcomes(signal.Outcomes);
    }

    private async ValueTask OnCounterFlowStepCanceledAsync(CancelSignal signal, SignalContext context)
    {
        var flowchartContext = context.ReceiverStepExecutionContext;
        await CompleteIfNoPendingWorkAsync(flowchartContext).ConfigureAwait(false);
        var flowchart = (Flowchart)flowchartContext.Step;
        var flowGraph = flowchartContext.GetFlowGraph();
        var flowScope = flowchart.GetFlowScope(flowchartContext);

        // Propagate canceled connections visited count by scheduling with Outcomes.Empty
        await MaybeScheduleOutboundStepsAsync(flowGraph, flowScope, flowchartContext, context.SenderStepExecutionContext.Step, context.SenderStepExecutionContext, Outcomes.Empty, OnChildCompletedAsync).ConfigureAwait(false);
    }
}