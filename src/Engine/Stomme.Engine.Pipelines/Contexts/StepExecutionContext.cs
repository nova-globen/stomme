using System.Runtime.CompilerServices;
using Stomme.Engine.Common;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents the context of a step execution.
/// </summary>
[PublicAPI]
public partial class StepExecutionContext : IExecutionContext, IDisposable
{
    private readonly ISystemClock _systemClock;
    private StepStatus _status;
    private Exception? _exception;
    private long _executionCount;
    private StepExecutionContext? _parentStepExecutionContext;
    
    // Bookmarks created during the lifetime of this step. 
    private List<Bookmark> _newBookmarks = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="StepExecutionContext"/> class.
    /// </summary>
    public StepExecutionContext(
        string id,
        PipelineExecutionContext pipelineExecutionContext,
        StepExecutionContext? parentStepExecutionContext,
        IStep step,
        StepDescriptor stepDescriptor,
        DateTimeOffset startedAt,
        object? tag,
        ISystemClock systemClock,
        CancellationToken cancellationToken)
    {
        _systemClock = systemClock;
        Properties = new ChangeTrackingDictionary<string, object>(Taint);
        Metadata = new ChangeTrackingDictionary<string, object>(Taint);
        StepState = new ChangeTrackingDictionary<string, object>(Taint);
        StepInput = new ChangeTrackingDictionary<string, object>(Taint);
        PipelineExecutionContext = pipelineExecutionContext;
        ParentStepExecutionContext = parentStepExecutionContext;
        var expressionExecutionContextProps = ExpressionExecutionContextExtensions.CreateStepExecutionContextPropertiesFrom(pipelineExecutionContext, pipelineExecutionContext.Input);
        expressionExecutionContextProps[ExpressionExecutionContextExtensions.StepKey] = step;
        ExpressionExecutionContext = new(pipelineExecutionContext.ServiceProvider, new(), parentStepExecutionContext?.ExpressionExecutionContext ?? pipelineExecutionContext.ExpressionExecutionContext, expressionExecutionContextProps, Taint, CancellationToken);
        Step = step;
        StepDescriptor = stepDescriptor;
        StartedAt = startedAt;
        Status = StepStatus.Pending;
        Tag = tag;
        CancellationToken = cancellationToken;
        Id = id;
        _publisher = GetRequiredService<INotificationSender>();
    }

    /// <summary>
    /// The ID of the current step execution context.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// The time at which the step execution context was created.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// An optional tag to associate with the step execution.
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>
    /// The time at which the step execution context was completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Returns true if the step execution context has completed.
    /// </summary>
    public bool IsCompleted => Status is StepStatus.Completed or StepStatus.Canceled;

    /// <summary>
    /// Gets or sets a value indicating whether the step is actively executing. 
    /// </summary>
    /// <remarks>
    /// This flag is set to <c>true</c> immediately before the step begins execution 
    /// and is set to <c>false</c> once the execution is completed. 
    /// It can be used to determine if a step was in-progress in case of unexpected 
    /// application termination, allowing the system to retry execution upon restarting. 
    /// </remarks>
    public bool IsExecuting { get; set; }

    /// <summary>
    /// The number of faults encountered during the execution of the step and its descendants.
    /// </summary>
    public int AggregateFaultCount { get; set; }

    /// <summary>
    /// The pipeline execution context. 
    /// </summary>
    public PipelineExecutionContext PipelineExecutionContext { get; }

    /// <summary>
    /// The parent step execution context, if any.
    /// This represents the structural container step (e.g., Flowchart contains all its children).
    /// </summary>
    public StepExecutionContext? ParentStepExecutionContext
    {
        get => _parentStepExecutionContext;
        internal set
        {
            _parentStepExecutionContext = value;
            _parentStepExecutionContext?.Children.Add(this);
        }
    }

    /// <summary>
    /// The ID of the step execution context that scheduled this step execution.
    /// This represents the temporal/execution predecessor that directly triggered execution of this step,
    /// distinct from <see cref="ParentStepExecutionContext"/> which represents the structural container.
    /// For example, in a Flowchart, Step B might complete and schedule Step C, making B the scheduling step for C.
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }

    /// <summary>
    /// The ID of the step that scheduled this step execution (denormalized for convenience).
    /// This is the Step.Id from the step execution context identified by <see cref="SchedulingStepExecutionId"/>.
    /// </summary>
    public string? SchedulingStepId { get; set; }

    /// <summary>
    /// The pipeline instance ID of the pipeline that scheduled this step execution.
    /// This is set when crossing pipeline boundaries (e.g., via ExecutePipeline or DispatchPipeline).
    /// For steps within the same pipeline instance, this will be null.
    /// </summary>
    public string? SchedulingPipelineInstanceId { get; set; }

    /// <summary>
    /// The depth of this step in the call stack (0 for root steps).
    /// </summary>
    public int CallStackDepth { get; set; }

    /// <summary>
    /// The expression execution context.
    /// </summary>
    public ExpressionExecutionContext ExpressionExecutionContext { get; }

    /// <inheritdoc />
    public IEnumerable<Variable> Variables
    {
        get
        {
            var containerVariables = (Step as IVariableContainer)?.Variables ?? Enumerable.Empty<Variable>();
            var dynamicVariables = DynamicVariables;
            var mergedVariables = new Dictionary<string, Variable>();
            
            foreach (var containerVariable in containerVariables)
            {
                var name = !string.IsNullOrEmpty(containerVariable.Name) ? containerVariable.Name : containerVariable.Id;
                mergedVariables[name] = containerVariable;
            }
            
            foreach (var dynamicVariable in dynamicVariables)
            {
                var name = !string.IsNullOrEmpty(dynamicVariable.Name) ? dynamicVariable.Name : dynamicVariable.Id;
                mergedVariables[name] = dynamicVariable;
            }
            return mergedVariables.Values;
        }
    }

    /// <summary>
    /// A list of variables that are dynamically added to the step execution context.
    /// </summary>
    public ICollection<Variable> DynamicVariables { get; set; } = new List<Variable>();

    /// <summary>
    /// The currently executing step.
    /// </summary>
    public IStep Step { get; set; }

    /// <summary>
    /// The step descriptor.
    /// </summary>
    public StepDescriptor StepDescriptor { get; }

    /// <summary>
    /// A cancellation token to use when invoking asynchronous operations.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// The current status of the step.
    /// </summary>
    public StepStatus Status
    {
        get => _status;
        private set
        {
            if (value == _status)
                return;

            _status = value;
            Taint();
        }
    }

    public IDisposable EnterExecution()
    {
        return new PipelineExecutionState(this);
    }

    /// <summary>
    /// Sets the current status of the step.
    /// </summary>
    public void TransitionTo(StepStatus status)
    {
        Status = status;
    }

    /// <summary>
    /// Gets or sets the exception that occurred during the step execution, if any.
    /// </summary>
    public Exception? Exception
    {
        get => _exception;
        set
        {
            _exception = value;
            Taint();
        }
    }

    /// <inheritdoc />
    public IDictionary<string, object> Properties { get; private set; }

    /// <summary>
    /// A transient dictionary of values that can be associated with this step execution context.
    /// These properties only exist while the step executes and are not persisted. 
    /// </summary>
    public IDictionary<object, object> TransientProperties { get; set; } = new Dictionary<object, object>();
    
    /// <summary>
    /// A dictionary of metadata about this execution. In contrast to <see cref="Properties"/>, this metadata is never pruned and must be kept lightweight.
    /// </summary>
    public IDictionary<string, object> Metadata { get; private set; }

    /// <summary>
    /// Returns the <see cref="StepNode"/> metadata about the current step.
    /// </summary>
    public StepNode StepNode => PipelineExecutionContext.FindNodeByStep(Step)!;

    /// <summary>
    /// Returns the global node ID for the current step within the graph.
    /// </summary>
    /// <remarks>As of tool version 3.0, all step IDs are already unique, so there's no need to construct a hierarchical ID</remarks>
    public string NodeId => StepNode.NodeId;

    public ISet<StepExecutionContext> Children { get; } = new HashSet<StepExecutionContext>();

    /// <summary>
    /// A list of bookmarks associated with the current step.
    /// </summary>
    public IEnumerable<Bookmark> Bookmarks => PipelineExecutionContext.Bookmarks.Where(x => x.StepInstanceId == Id);

    /// <summary>
    /// A collection of bookmarks created during the execution of the step.
    /// </summary>
    public IEnumerable<Bookmark> NewBookmarks => _newBookmarks.AsReadOnly();

    /// <summary>
    /// The number of times this <see cref="StepExecutionContext"/> has executed.
    /// </summary>
    public long ExecutionCount => _executionCount;

    /// <summary>
    /// A dictionary of received inputs for the current pipeline.
    /// </summary>
    public IDictionary<string, object> PipelineInput => PipelineExecutionContext.Input;

    /// <summary>
    /// A dictionary of inputs for the current step.
    /// </summary>
    public IDictionary<string, object> StepInput { get; private set; }

    /// <summary>
    /// Journal data will be added to the pipeline execution log for the "Executed" event.  
    /// </summary>
    // ReSharper disable once CollectionNeverQueried.Global
    public IDictionary<string, object> JournalData { get; } = new Dictionary<string, object>();

    /// <summary>
    /// Stores the evaluated inputs, serialized, for the current step for historical purposes.
    /// </summary>
    public IDictionary<string, object> StepState { get; }

    /// <summary>
    /// Indicates whether the state of the current step execution context has been modified.
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Schedules the specified step to be executed.
    /// </summary>
    /// <param name="step">The step to schedule.</param>
    /// <param name="completionCallback">An optional callback to invoke when the step completes.</param>
    /// <param name="tag">An optional tag to associate with the step execution.</param>
    /// <param name="variables">An optional list of variables to declare with the step execution.</param>
    public ValueTask ScheduleStepAsync(IStep? step, StepCompletionCallback? completionCallback, object? tag = null, IEnumerable<Variable>? variables = null)
    {
        var options = new ScheduleWorkOptions
        {
            CompletionCallback = completionCallback,
            Tag = tag,
            Variables = variables?.ToList()
        };
        return ScheduleStepAsync(step, options);
    }

    /// <summary>
    /// Schedules the specified step to be executed.
    /// </summary>
    /// <param name="step">The step to schedule.</param>
    /// <param name="options">The options used to schedule the step.</param>
    public async ValueTask ScheduleStepAsync(IStep? step, ScheduleWorkOptions? options = null)
    {
        await ScheduleStepAsync(step, this, options).ConfigureAwait(false);
    }

    /// <summary>
    /// Schedules the specified step to be executed.
    /// </summary>
    /// <param name="step">The step to schedule.</param>
    /// <param name="owner">The step execution context that owns the scheduled step.</param>
    /// <param name="options">The options used to schedule the step.</param>
    public async ValueTask ScheduleStepAsync(IStep? step, StepExecutionContext? owner, ScheduleWorkOptions? options = null)
    {
        var schedulerStrategy = GetRequiredService<IStepExecutionContextSchedulerStrategy>();
        await schedulerStrategy.ScheduleStepAsync(this, step, owner, options).ConfigureAwait(false);
    }

    /// <summary>
    /// Schedules the specified step to be executed.
    /// </summary>
    /// <param name="stepNode">The step node to schedule.</param>
    /// <param name="owner">The step execution context that owns the scheduled step.</param>
    /// <param name="options">The options used to schedule the step.</param>
    public async Task ScheduleStepAsync(StepNode? stepNode, StepExecutionContext? owner = null, ScheduleWorkOptions? options = null)
    {
        var schedulerStrategy = GetRequiredService<IStepExecutionContextSchedulerStrategy>();
        await schedulerStrategy.ScheduleStepAsync(this, stepNode, owner, options).ConfigureAwait(false);
    }

    /// <summary>
    /// Schedules the specified steps to be executed.
    /// </summary>
    /// <param name="steps">The steps to schedule.</param>
    public async ValueTask ScheduleStepsAsync(params IStep?[] steps) => await ScheduleSteps(steps).ConfigureAwait(false);

    /// <summary>
    /// Schedules the specified steps to be executed.
    /// </summary>
    /// <param name="steps">The steps to schedule.</param>
    /// <param name="completionCallback">The callback to invoke when the steps complete.</param>
    /// <param name="tag">An optional tag to associate with the step execution.</param>
    /// <param name="variables">An optional list of variables to declare with the step execution.</param>
    public ValueTask ScheduleSteps(IEnumerable<IStep?> steps, StepCompletionCallback? completionCallback, object? tag = null, IEnumerable<Variable>? variables = null)
    {
        var options = new ScheduleWorkOptions
        {
            CompletionCallback = completionCallback,
            Tag = tag,
            Variables = variables?.ToList()
        };
        return ScheduleSteps(steps, options);
    }

    /// <summary>
    /// Schedules the specified steps to be executed.
    /// </summary>
    /// <param name="steps">The steps to schedule.</param>
    /// <param name="options">The options used to schedule the steps.</param>
    public async ValueTask ScheduleSteps(IEnumerable<IStep?> steps, ScheduleWorkOptions? options = null)
    {
        foreach (var step in steps)
            await ScheduleStepAsync(step, options).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a bookmark for each payload.
    /// </summary>
    /// <param name="payloads">The payloads to create bookmarks for.</param>
    /// <param name="callback">An optional callback that is invoked when the bookmark is resumed.</param>
    /// <param name="includeStepInstanceId">Whether or not the step instance ID should be included in the bookmark payload.</param>
    /// <param name="bookmarkName">An optional name to use for the bookmark. Defaults to the step type.</param>
    public void CreateBookmarks(IEnumerable<object> payloads, ExecuteStepDelegate? callback = null, bool includeStepInstanceId = true, string? bookmarkName = null)
    {
        foreach (var payload in payloads)
            CreateBookmark(new()
            {
                Stimulus = payload,
                Callback = callback,
                BookmarkName = bookmarkName,
                IncludeStepInstanceId = includeStepInstanceId
            });
    }

    /// <summary>
    /// Adds each bookmark to the list of bookmarks.
    /// </summary>
    /// <param name="bookmarks">The bookmarks to add.</param>
    public void AddBookmarks(IEnumerable<Bookmark> bookmarks)
    {
        PipelineExecutionContext.Bookmarks.AddRange(bookmarks);
        Taint();
    }

    /// <summary>
    /// Adds a bookmark to the list of bookmarks.
    /// </summary>
    /// <param name="bookmark">The bookmark to add.</param>
    public void AddBookmark(Bookmark bookmark)
    {
        _newBookmarks.Add(bookmark);
        PipelineExecutionContext.Bookmarks.Add(bookmark);
        Taint();
    }

    /// <summary>
    /// Creates a bookmark so that this step can be resumed at a later time.
    /// </summary>
    /// <param name="callback">An optional callback that is invoked when the bookmark is resumed.</param>
    /// <param name="metadata">Custom properties to associate with the bookmark.</param>
    /// <returns>The created bookmark.</returns>
    public Bookmark CreateBookmark(ExecuteStepDelegate callback, IDictionary<string, string>? metadata = null)
    {
        return CreateBookmark(new()
        {
            Callback = callback,
            Metadata = metadata
        });
    }

    /// <summary>
    /// Creates a bookmark so that this step can be resumed at a later time.
    /// </summary>
    /// <param name="stimulus">The payload to associate with the bookmark.</param>
    /// <param name="callback">An optional callback that is invoked when the bookmark is resumed.</param>
    /// <param name="includeStepInstanceId">Whether or not the step instance ID should be included in the bookmark payload.</param>
    /// <param name="customProperties">Custom properties to associate with the bookmark.</param>
    /// <returns>The created bookmark.</returns>
    public Bookmark CreateBookmark(object stimulus, ExecuteStepDelegate? callback, bool includeStepInstanceId = true, IDictionary<string, string>? customProperties = null)
    {
        return CreateBookmark(new()
        {
            Stimulus = stimulus,
            Callback = callback,
            IncludeStepInstanceId = includeStepInstanceId,
            Metadata = customProperties
        });
    }

    /// <summary>
    /// Creates a bookmark for the current step execution context.
    /// </summary>
    /// <param name="stimulus">The payload to associate with the bookmark.</param>
    /// <param name="includeStepInstanceId">Specifies whether to include the step instance ID in the bookmark information. Defaults to true.</param>
    /// <param name="customProperties">Additional custom properties to associate with the bookmark. Defaults to null.</param>
    /// <returns>The created bookmark.</returns>
    public Bookmark CreateBookmark(object stimulus, bool includeStepInstanceId, IDictionary<string, string>? customProperties = null)
    {
        return CreateBookmark(new()
        {
            Stimulus = stimulus,
            IncludeStepInstanceId = includeStepInstanceId,
            Metadata = customProperties
        });
    }

    /// <summary>
    /// Creates a bookmark so that this step can be resumed at a later time. 
    /// </summary>
    /// <param name="stimulus">The payload to associate with the bookmark.</param>
    /// <param name="metadata">Custom properties to associate with the bookmark.</param>
    /// <returns>The created bookmark.</returns>
    public Bookmark CreateBookmark(object stimulus, IDictionary<string, string>? metadata = null)
    {
        return CreateBookmark(new()
        {
            Stimulus = stimulus,
            Metadata = metadata
        });
    }

    /// <summary>
    /// Creates a bookmark so that this step can be resumed at a later time.
    /// Creating a bookmark will automatically suspend the pipeline after all pending steps have executed.
    /// </summary>
    public Bookmark CreateBookmark(CreateBookmarkArgs? options = null)
    {
        var payload = options?.Stimulus;
        var callback = options?.Callback;
        var bookmarkName = options?.BookmarkName ?? Step.Type;
        var bookmarkHasher = GetRequiredService<IStimulusHasher>();
        var identityGenerator = GetRequiredService<IIdentityGenerator>();
        var includeStepInstanceId = options?.IncludeStepInstanceId ?? true;
        var hash = bookmarkHasher.Hash(bookmarkName, payload, includeStepInstanceId ? Id : null);
        var bookmarkId = options?.BookmarkId ?? identityGenerator.GenerateId();

        var bookmark = new Bookmark(
            bookmarkId,
            bookmarkName,
            hash,
            payload,
            Step.Id,
            StepNode.NodeId,
            Id,
            _systemClock.UtcNow,
            options?.AutoBurn ?? true,
            callback?.Method.Name,
            options?.AutoComplete ?? true,
            options?.Metadata);

        AddBookmark(bookmark);
        return bookmark;
    }

    /// <summary>
    /// Clear all bookmarks.
    /// </summary>
    public void ClearBookmarks()
    {
        _newBookmarks.Clear();
        PipelineExecutionContext.Bookmarks.RemoveWhere(x => x.StepInstanceId == Id);
        Taint();
    }

    /// <summary>
    /// Returns a property value associated with the current step context. 
    /// </summary>
    public T? GetProperty<T>(string key) => Properties.TryGetValue<T?>(key, out var value) ? value : default;

    /// <summary>
    /// Returns a property value associated with the current step context. 
    /// </summary>
    public T GetProperty<T>(string key, Func<T> defaultValue)
    {
        if (Properties.TryGetValue<T?>(key, out var value))
            return value!;

        value = defaultValue();
        Properties[key] = value!;

        return value!;
    }

    /// <summary>
    /// Stores a property associated with the current step context. 
    /// </summary>
    public void SetProperty<T>(string key, T? value) => Properties[key] = value!;

    /// <summary>
    /// Updates a property associated with the current step context. 
    /// </summary>
    public T UpdateProperty<T>(string key, Func<T?, T> updater) where T : notnull
    {
        var value = GetProperty<T?>(key);
        value = updater(value);
        Properties[key] = value;
        return value;
    }

    /// <summary>
    /// Removes a property associated with the current step context.
    /// </summary>
    /// <param name="key">The property key.</param>
    public void RemoveProperty(string key) => Properties.Remove(key);
    
    /// <summary>
    /// Returns a metadata value associated with the current step context. 
    /// </summary>
    public T? GetMetadata<T>(string key) => Metadata.TryGetValue<T?>(key, out var value) ? value : default;

    /// <summary>
    /// Returns a metadata value associated with the current step context. 
    /// </summary>
    public T GetMetadata<T>(string key, Func<T> defaultValue)
    {
        if (Metadata.TryGetValue<T?>(key, out var value))
            return value!;

        value = defaultValue();
        Metadata[key] = value!;

        return value!;
    }

    /// <summary>
    /// Stores a metadata value associated with the current step context. 
    /// </summary>
    public void SetMetadata<T>(string key, T? value) => Metadata[key] = value!;

    /// <summary>
    /// Updates a metadata value associated with the current step context. 
    /// </summary>
    public T UpdateMetadata<T>(string key, Func<T?, T> updater) where T : notnull
    {
        var value = GetMetadata<T?>(key);
        value = updater(value);
        Metadata[key] = value;
        return value;
    }

    /// <summary>
    /// Removes a metadata value associated with the current step context.
    /// </summary>
    /// <param name="key">The metadata key.</param>
    public void RemoveMetadata(string key) => Metadata.Remove(key);

    /// <summary>
    /// Resolves a required service using the service provider.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The resolved service.</returns>
    public T GetRequiredService<T>() where T : notnull => PipelineExecutionContext.GetRequiredService<T>();

    /// <summary>
    /// Resolves a required service using the service provider.
    /// </summary>
    /// <param name="serviceType">The service type.</param>
    /// <returns>The resolved service.</returns>
    public object GetRequiredService(Type serviceType) => PipelineExecutionContext.GetRequiredService(serviceType);

    /// <summary>
    /// Resolves a service using the service provider. If not found, a new instance is created.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The resolved service.</returns>
    public T GetOrCreateService<T>() where T : notnull => PipelineExecutionContext.GetOrCreateService<T>();

    /// <summary>
    /// Resolves a service using the service provider. If not found, a new instance is created.
    /// </summary>
    /// <param name="serviceType">The service type.</param>
    /// <returns>The resolved service.</returns>
    public object GetOrCreateService(Type serviceType) => PipelineExecutionContext.GetOrCreateService(serviceType);

    /// <summary>
    /// Resolves a service using the service provider.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The resolved service.</returns>
    public T? GetService<T>() where T : notnull => PipelineExecutionContext.GetService<T>();

    /// <summary>
    /// Resolves all services of the specified type using the service provider.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The resolved services.</returns>
    public IEnumerable<T> GetServices<T>() where T : notnull => PipelineExecutionContext.GetServices<T>();

    /// <summary>
    /// Resolves a service using the service provider.
    /// </summary>
    /// <param name="serviceType">The service type.</param>
    /// <returns>The resolved service.</returns>
    public object? GetService(Type serviceType) => PipelineExecutionContext.GetService(serviceType);

    /// <summary>
    /// Gets the value of the specified input.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <typeparam name="T">The type of the input.</typeparam>
    /// <returns>The input value.</returns>
    public T? Get<T>(Input<T>? input) => input == null ? default : Get<T>(input.MemoryBlockReference());

    /// <summary>
    /// Gets the value of the specified output.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <typeparam name="T">The type of the output.</typeparam>
    /// <returns>The output value.</returns>
    public T? Get<T>(Output<T>? output) => output == null ? default : Get<T>(output.MemoryBlockReference());

    /// <summary>
    /// Gets the value of the specified output.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <returns>The output value.</returns>
    public object? Get(Output? output) => output == null ? null : Get(output.MemoryBlockReference());

    /// <summary>
    /// Gets the value of the specified memory block.
    /// </summary>
    /// <param name="blockReference">The memory block reference.</param>
    /// <returns>The memory block value.</returns>
    /// <exception cref="InvalidOperationException">The memory block does not exist.</exception>
    public object? Get(MemoryBlockReference blockReference)
    {
        return !TryGet(blockReference, out var value)
            ? throw new InvalidOperationException($"The memory block '{blockReference}' does not exist.")
            : value;
    }

    /// <summary>
    /// Gets the value of the specified memory block.
    /// </summary>
    /// <param name="blockReference">The memory block reference.</param>
    /// <typeparam name="T">The type of the memory block.</typeparam>
    /// <returns>The memory block value.</returns>
    public T? Get<T>(MemoryBlockReference blockReference)
    {
        var value = Get(blockReference);
        return value != null ? value.ConvertTo<T>() : default;
    }

    /// <summary>
    /// Tries to get the value of the specified memory block.
    /// </summary>
    /// <param name="blockReference">The memory block reference.</param>
    /// <param name="value">The memory block value.</param>
    /// <returns>True if the memory block exists, false otherwise.</returns>
    public bool TryGet(MemoryBlockReference blockReference, out object? value)
    {
        // First, try to get the value from the memory register
        var memoryBlock = GetMemoryBlock(blockReference);

        if (memoryBlock != null)
        {
            value = memoryBlock.Value;
            return true;
        }

        // Handle Literal references as a fallback - they can hold their value directly
        if (blockReference is Literal literal)
        {
            value = literal.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Sets a value at the specified memory block.
    /// </summary>
    /// <param name="blockReference">The memory block reference.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="configure">An optional callback that can be used to configure the memory block.</param>
    public void Set(MemoryBlockReference blockReference, object? value, Action<MemoryBlock>? configure = null) => ExpressionExecutionContext.Set(blockReference, value, configure);

    /// <summary>
    /// Sets a value at the specified output.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="outputName">The name of the output.</param>
    /// <typeparam name="T">The type of the output.</typeparam>
    public void Set<T>(Output<T>? output, T? value, [CallerArgumentExpression("output")] string? outputName = null) => Set((Output?)output, value, outputName);

    /// <summary>
    /// Sets a value at the specified output.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="outputName">The name of the output.</param>
    public void Set(Output? output, object? value, [CallerArgumentExpression("output")] string? outputName = null)
    {
        // Store the value in the expression execution memory block.
        ExpressionExecutionContext.Set(output, value);

        // Also store the value in the pipeline execution transient step output register.
        PipelineExecutionContext.RecordStepOutput(this, outputName, value);
    }

    /// <summary>
    /// Removes all completion callbacks for the current step.
    /// </summary>
    public void ClearCompletionCallbacks()
    {
        var entriesToRemove = PipelineExecutionContext.CompletionCallbacks.Where(x => x.Owner == this).ToList();
        PipelineExecutionContext.RemoveCompletionCallbacks(entriesToRemove);
    }

    public void Taint()
    {
        if (!IsDirty)
            IsDirty = true;
    }

    public void ClearTaint()
    {
        if (IsDirty)
            IsDirty = false;
    }

    internal void IncrementExecutionCount()
    {
        _executionCount++;
    }

    private MemoryBlock? GetMemoryBlock(MemoryBlockReference locationBlockReference)
    {
        return ExpressionExecutionContext.TryGetBlock(locationBlockReference, out var memoryBlock) ? memoryBlock : null;
    }

    void IDisposable.Dispose()
    {
    }
}

