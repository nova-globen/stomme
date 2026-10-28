using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Options;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Steps;

/// <summary>
/// A flowchart consists of a collection of steps and connections between them.
/// </summary>
[Step("Stomme", "Flow", "A flowchart is a collection of steps and connections between them.")]
[Browsable(false)]
public partial class Flowchart : Container
{
    /// <summary>
    /// The property key used to store the flowchart execution mode in <see cref="PipelineExecutionContext.Properties"/>.
    /// </summary>
    public const string ExecutionModePropertyKey = "Flowchart:ExecutionMode";

    /// <summary>
    /// Set this to <c>false</c> from your program file in case you wish to use the old counter based model.
    /// This static field is used as a final fallback when no execution mode is specified via options or pipeline execution context properties.
    /// Note: Prefer using <see cref="FlowchartOptions"/> configured via DI for application-wide settings.
    /// </summary>
    // ReSharper disable once GrammarMistakeInComment
    public static bool UseTokenFlow = false; // Default to false to maintain the same behavior with 3.5.2 out of the box.

    /// <inheritdoc />
    public Flowchart([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        OnSignalReceived<ScheduleStepOutcomes>(OnScheduleOutcomesAsync);
        OnSignalReceived<ScheduleChildStep>(OnScheduleChildStepAsync);
        OnSignalReceived<CancelSignal>(OnStepCanceledAsync);
    }

    /// <summary>
    /// The step to execute when the flowchart starts.
    /// </summary>
    [Port] [Browsable(false)] public IStep? Start { get; set; }

    /// <summary>
    /// A list of connections between steps.
    /// </summary>
    public ICollection<Connection> Connections { get; set; } = new List<Connection>();

    /// <inheritdoc />
    protected override async ValueTask ScheduleChildrenAsync(StepExecutionContext context)
    {
        var startStep = GetStartStep(context);

        if (startStep == null)
        {
            // Nothing else to execute.
            await context.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        await context.ScheduleStepAsync(startStep, OnChildCompletedAsync).ConfigureAwait(false);
    }

    private async ValueTask OnScheduleChildStepAsync(ScheduleChildStep signal, SignalContext context)
    {
        var flowchartContext = context.ReceiverStepExecutionContext;
        var step = signal.Step;
        var stepExecutionContext = signal.StepExecutionContext;

        if (stepExecutionContext != null)
        {
            await flowchartContext.ScheduleStepAsync(stepExecutionContext.Step, new()
            {
                ExistingStepExecutionContext = stepExecutionContext,
                CompletionCallback = OnChildCompletedAsync,
                Input = signal.Input
            }).ConfigureAwait(false);
        }
        else
        {
            await flowchartContext.ScheduleStepAsync(step, new()
            {
                CompletionCallback = OnChildCompletedAsync,
                Input = signal.Input
            }).ConfigureAwait(false);
        }
    }

    private ValueTask OnChildCompletedAsync(StepCompletedContext context)
    {
        return ExecuteBasedOnMode(
            context.TargetContext,
            () => OnChildCompletedTokenBasedLogicAsync(context),
            () => OnChildCompletedCounterBasedLogicAsync(context));
    }

    private ValueTask OnStepCanceledAsync(CancelSignal signal, SignalContext context)
    {
        return ExecuteBasedOnMode(
            context.ReceiverStepExecutionContext,
            () => OnTokenFlowStepCanceledAsync(signal, context),
            () => OnCounterFlowStepCanceledAsync(signal, context));
    }

    private async Task CompleteIfNoPendingWorkAsync(StepExecutionContext context)
    {
        var hasPendingWork = HasPendingWork(context);

        if (!hasPendingWork)
        {
            var hasFaultedSteps = context.Children.Any(x => x.Status == StepStatus.Faulted);

            if (!hasFaultedSteps)
            {
                await context.CompleteStepAsync().ConfigureAwait(false);
            }
        }
    }

    private static ValueTask ExecuteBasedOnMode(StepExecutionContext context, Func<ValueTask> tokenBasedAction, Func<ValueTask> counterBasedAction)
    {
        var mode = GetEffectiveExecutionMode(context);

        return mode switch
        {
            FlowchartExecutionMode.TokenBased => tokenBasedAction(),
            FlowchartExecutionMode.CounterBased or FlowchartExecutionMode.Default or _ => counterBasedAction()
        };
    }

    /// <summary>
    /// Gets the effective execution mode for this flowchart execution.
    /// Priority: PipelineExecutionContext.Properties > FlowchartOptions (DI) > Static UseTokenFlow flag
    /// </summary>
    private static FlowchartExecutionMode GetEffectiveExecutionMode(StepExecutionContext context)
    {
        var pipelineExecutionContext = context.PipelineExecutionContext;

        if (!pipelineExecutionContext.Properties.TryGetValue(ExecutionModePropertyKey, out var modeValue))
            return GetDefaultModeFromOptions(context);

        var mode = ParseExecutionMode(modeValue);
        return mode != FlowchartExecutionMode.Default ? mode : GetDefaultModeFromOptions(context);
    }

    private static FlowchartExecutionMode ParseExecutionMode(object modeValue)
    {
        return modeValue switch
        {
            FlowchartExecutionMode executionMode => executionMode,
            string str when Enum.TryParse<FlowchartExecutionMode>(str, true, out var parsed) => parsed,
            int intValue when Enum.IsDefined(typeof(FlowchartExecutionMode), intValue) => (FlowchartExecutionMode)intValue,
            _ => FlowchartExecutionMode.Default
        };
    }

    private static FlowchartExecutionMode GetDefaultModeFromOptions(StepExecutionContext context)
    {
        var options = context.PipelineExecutionContext.ServiceProvider.GetService<IOptions<FlowchartOptions>>();
        var mode = options?.Value.DefaultExecutionMode ?? FlowchartExecutionMode.Default;
        if (mode == FlowchartExecutionMode.Default)
            return UseTokenFlow ? FlowchartExecutionMode.TokenBased : FlowchartExecutionMode.CounterBased;
        return mode;
    }
}