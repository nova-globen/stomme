using System.Diagnostics;
using System.Text.Json.Serialization;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Behaviors;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Serialization.Converters;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Base class for custom steps.
/// </summary>
[DebuggerDisplay("{Type} - {Id}")]
[UsedImplicitly(ImplicitUseTargetFlags.WithInheritors | ImplicitUseTargetFlags.Members)]
public abstract class Step : IStep, ISignalHandler
{
    private readonly ICollection<SignalHandlerRegistration> _signalReceivedHandlers = new List<SignalHandlerRegistration>();

    /// <summary>
    /// Constructor.
    /// </summary>
    protected Step(string? source = null, int? line = null)
    {
        this.SetSource(source, line);
        Type = StepTypeNameHelper.GenerateTypeName(GetType());
        Version = 1;
        Behaviors.Add<ScheduledChildCallbackBehavior>(this);
    }

    /// <inheritdoc />
    protected Step(string stepType, int version = 1, string? source = null, int? line = null) : this(source, line)
    {
        Type = stepType;
        Version = version;
    }

    /// <inheritdoc />
    public string Id { get; set; } = null!;
    
    /// <inheritdoc />
    public string NodeId { get; set; } = null!;
    
    /// <inheritdoc />
    public string? Name { get; set; }

    /// <inheritdoc />
    public string Type { get; set; }

    /// <inheritdoc />
    public int Version { get; set; }

    /// <summary>
    /// A flag indicating whether this step can be used for starting a pipeline.
    /// Usually used for triggers, but also used to disambiguate between two or more starting steps and no starting step was specified.
    /// </summary>
    [JsonIgnore]
    public bool CanStartPipeline
    {
        get => this.GetCanStartPipeline();
        set => this.SetCanStartPipeline(value);
    }

    /// <summary>
    /// A flag indicating if this step should execute synchronously or asynchronously.
    /// By default, steps with an <see cref="StepKind"/> of <see cref="Action"/>, <see cref="Task"/> or <see cref="Trigger"/>
    /// will execute synchronously, while steps of the <see cref="StepKind.Job"/> kind will execute asynchronously.
    /// </summary>
    [JsonIgnore]
    public bool? RunAsynchronously
    {
        get => this.GetRunAsynchronously();
        set => this.SetRunAsynchronously(value);
    }
    
    [JsonIgnore]
    public string? CommitStrategy
    {
        get => this.GetCommitStrategy();
        set => this.SetCommitStrategy(value);
    }

    /// <inheritdoc />
    [JsonConverter(typeof(PolymorphicObjectConverterFactory))]
    public IDictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();
    
    /// <inheritdoc />
    [JsonIgnore]
    public IDictionary<string, object> SyntheticProperties { get; set; } = new Dictionary<string, object>();
    
    /// <inheritdoc />
    [JsonConverter(typeof(PolymorphicObjectConverterFactory))]
    public IDictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// A collection of reusable behaviors to add to this step.
    /// </summary>
    [JsonIgnore]
    public ICollection<IBehavior> Behaviors { get; } = new List<IBehavior>();

    /// <summary>
    /// Override this method to return a value indicating whether the step can execute.
    /// </summary>
    protected virtual ValueTask<bool> CanExecuteAsync(StepExecutionContext context)
    {
        var result = CanExecute(context);
        return new(result);
    }
    
    /// <summary>
    /// Override this method to return a value indicating whether the step can execute.
    /// </summary>
    protected virtual bool CanExecute(StepExecutionContext context)
    {
        return true;
    }

    /// <summary>
    /// Override this method to implement step-specific logic.
    /// </summary>
    protected virtual ValueTask ExecuteAsync(StepExecutionContext context)
    {
        Execute(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Override this method to implement step-specific logic.
    /// </summary>
    protected virtual void Execute(StepExecutionContext context)
    {
    }

    /// <summary>
    /// Override this method to handle any signals sent from downstream steps.
    /// </summary>
    protected virtual ValueTask OnSignalReceivedAsync(object signal, SignalContext context)
    {
        OnSignalReceived(signal, context);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Override this method to handle any signals sent from downstream steps.
    /// </summary>
    protected virtual void OnSignalReceived(object signal, SignalContext context)
    {
    }

    /// <summary>
    /// Register a signal handler delegate.
    /// </summary>
    protected void OnSignalReceived(Type signalType, Func<object, SignalContext, ValueTask> handler) => _signalReceivedHandlers.Add(new SignalHandlerRegistration(signalType, handler));

    /// <summary>
    /// Register a signal handler delegate.
    /// </summary>
    protected void OnSignalReceived<T>(Func<T, SignalContext, ValueTask> handler) => OnSignalReceived(typeof(T), (signal, context) => handler((T)signal, context));

    /// <summary>
    /// Register a signal handler delegate.
    /// </summary>
    protected void OnSignalReceived<T>(Action<T, SignalContext> handler)
    {
        OnSignalReceived<T>((signal, context) =>
        {
            handler(signal, context);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Notify the pipeline that this step completed.
    /// </summary>
    protected async ValueTask CompleteAsync(StepExecutionContext context)
    {
        await context.CompleteStepAsync().ConfigureAwait(false);
    }
    
    async ValueTask<bool> IStep.CanExecuteAsync(StepExecutionContext context)
    {
        return await CanExecuteAsync(context).ConfigureAwait(false);
    }

    async ValueTask IStep.ExecuteAsync(StepExecutionContext context)
    {
        await ExecuteAsync(context).ConfigureAwait(false);

        // Invoke behaviors.
        foreach (var behavior in Behaviors) await behavior.ExecuteAsync(context).ConfigureAwait(false);
    }
    
    async ValueTask ISignalHandler.ReceiveSignalAsync(object signal, SignalContext context)
    {
        // Give derived step a chance to do something with the signal.
        await OnSignalReceivedAsync(signal, context).ConfigureAwait(false);

        // Invoke registered signal delegates for this particular type of signal.
        var signalType = signal.GetType();
        var handlers = _signalReceivedHandlers.Where(x => x.SignalType == signalType);

        foreach (var registration in handlers)
            await registration.Handler(signal, context).ConfigureAwait(false);

        // Invoke behaviors.
        foreach (var behavior in Behaviors) await behavior.ReceiveSignalAsync(signal, context).ConfigureAwait(false);
    }
}