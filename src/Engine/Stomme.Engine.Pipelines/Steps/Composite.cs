using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Signals;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Represents a composite step that has a single <see cref="Root"/> step. Like a pipeline, but without pipeline-level properties.
/// </summary>
[PublicAPI]
public abstract class Composite : Step, IVariableContainer, IComposite
{
    /// <inheritdoc />
    protected Composite(string? source = null, int? line = null) : base(source, line)
    {
        OnSignalReceived<CompleteCompositeSignal>(OnCompleteCompositeSignal);
    }

    /// <inheritdoc />
    [JsonIgnore]  // Composite steps' Variables is intended to be constructed from code only.
    public ICollection<Variable> Variables { get; init; } = new List<Variable>();

    /// <summary>
    /// A variable to allow steps to set a result.
    /// </summary>
    [JsonIgnore]
    public Variable? ResultVariable { get; set; }
    
    /// <summary>
    /// The step to schedule when this step executes.
    /// </summary>
    [Port]
    [Browsable(false)]
    [JsonIgnoreCompositeRoot]
    public IStep Root { get; set; } = new Sequence();

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        ConfigureSteps(context);
        
        // Register variables.
        foreach (var variable in Variables) 
            variable.Set(context, variable.Value);

        await context.ScheduleStepAsync(Root, OnRootCompletedAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// Override this method to configure step properties before execution.
    /// </summary>
    protected virtual void ConfigureSteps(StepExecutionContext context)
    {
    }

    private async ValueTask OnRootCompletedAsync(StepCompletedContext context)
    {
        await OnCompletedAsync(context).ConfigureAwait(false);
        await context.TargetContext.CompleteStepAsync().ConfigureAwait(false);
    }
    
    /// <summary>
    /// Completes this composite step.
    /// </summary>
    protected async Task CompleteAsync(StepExecutionContext context, object? result = null) => await context.SendSignalAsync(new CompleteCompositeSignal(result)).ConfigureAwait(false);
    
    /// <summary>
    /// Completes this composite step.
    /// </summary>
    protected async Task CompleteAsync(StepExecutionContext context, params string[] outcomes) => await CompleteAsync(context, new Outcomes(outcomes)).ConfigureAwait(false);

    /// <summary>
    /// Override this method to execute custom logic when the composite step completes.
    /// </summary>
    /// <param name="context">The context of the composite step.</param>
    protected virtual ValueTask OnCompletedAsync(StepCompletedContext context)
    {
        OnCompleted(context);
        return new();
    }

    /// <summary>
    /// Override this method to execute custom logic when the composite step completes.
    /// </summary>
    /// <param name="context">The context of the composite step.</param>
    protected virtual void OnCompleted(StepCompletedContext context)
    {
    }

    private async ValueTask OnCompleteCompositeSignal(CompleteCompositeSignal signal, SignalContext context)
    {
        // Set the outcome into the context for the parent step to pick up.
        context.SenderStepExecutionContext.PipelineExecutionContext.TransientProperties[nameof(CompleteCompositeSignal)] = signal;
        
        var completedContext = new StepCompletedContext(context.ReceiverStepExecutionContext, context.SenderStepExecutionContext, signal.Value);
        await OnCompletedAsync(completedContext).ConfigureAwait(false);
        
        // Complete the sender first so that it notifies its parents to complete.
        await context.SenderStepExecutionContext.CompleteStepAsync().ConfigureAwait(false);
        
        // Then complete this step.
        await context.ReceiverStepExecutionContext.CompleteStepAsync(signal.Value).ConfigureAwait(false);
        context.StopPropagation();
        
    }

    /// <summary>
    /// Creates a new <see cref="Steps.Inline"/> step.
    /// </summary>
    protected static Inline Inline(Func<StepExecutionContext, ValueTask> step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(step, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.Inline"/> step.
    /// </summary>
    protected static Inline Inline(Func<ValueTask> step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(step, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.Inline"/> step.
    /// </summary>
    protected static Inline Inline(Action<StepExecutionContext> step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(step, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.Inline"/> step.
    /// </summary>
    protected static Inline Inline(Action step, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(step, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.Inline"/> step.
    /// </summary>
    protected static Inline<TResult> Inline<TResult>(Func<StepExecutionContext, ValueTask<TResult>> step, MemoryBlockReference? output = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(step, output, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.Inline"/> step.
    /// </summary>
    protected static Inline<TResult> Inline<TResult>(Func<ValueTask<TResult>> step, MemoryBlockReference? output = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(step, output, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.Inline"/> step.
    /// </summary>
    protected static Inline<TResult> Inline<TResult>(Func<StepExecutionContext, TResult> step, MemoryBlockReference? output, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(step, output, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.Inline"/> step.
    /// </summary>
    protected static Inline<TResult> Inline<TResult>(Func<TResult> step, MemoryBlockReference? output = null, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(step, output, source, line);

    /// <summary>
    /// Creates a new <see cref="Steps.SetVariable"/> step.
    /// </summary>
    protected static SetVariable<T> SetVariable<T>(Variable<T> variable, T value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(variable, value, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.SetVariable"/> step.
    /// </summary>
    protected static SetVariable<T> SetVariable<T>(Variable<T> variable, Func<ExpressionExecutionContext, T> value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(variable, value, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.SetVariable"/> step.
    /// </summary>
    protected static SetVariable<T> SetVariable<T>(Variable<T> variable, Func<T> value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(variable, value, source, line);
    
    /// <summary>
    /// Creates a new <see cref="Steps.SetVariable"/> step.
    /// </summary>
    protected static SetVariable<T> SetVariable<T>(Variable<T> variable, Variable<T> value, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) => new(variable, value, source, line);

    public virtual void Setup()
    {
    }
}

/// <summary>
/// Base class for custom steps with auto-complete behavior that return a result.
/// </summary>
[PublicAPI]
public abstract class CompositeWithResult : Composite
{
    /// <inheritdoc />
    protected CompositeWithResult(string? source = null, int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    protected CompositeWithResult(MemoryBlockReference? output, string? source = null, int? line = null) : base(source, line)
    {
        if (output != null) Result = new Output(output);
    }

    /// <inheritdoc />
    protected CompositeWithResult(Output? output, string? source = null, int? line = null) : base(source, line)
    {
        Result = output;
    }

    /// <summary>
    /// The result value of the composite.
    /// </summary>
    [Output] public Output? Result { get; set; }
}

/// <summary>
/// Represents a composite step that has a single <see cref="Composite.Root"/> step and returns a result.
/// </summary>
[PublicAPI]
public abstract class Composite<T> : Composite, IStepWithResult<T>
{
    /// <inheritdoc />
    protected Composite([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The result of the step.
    /// </summary>
    [Output] public Output<T>? Result { get; set; }

    Output? IStepWithResult.Result
    {
        get => Result;
        set => Result = (Output<T>?)value;
    }
}