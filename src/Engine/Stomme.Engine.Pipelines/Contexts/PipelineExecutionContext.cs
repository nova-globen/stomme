using System.Collections.ObjectModel;
using Stomme.Engine.Common;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.State;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// A delegate entry that is used by steps to be notified when the steps they scheduled are completed.
/// </summary>
/// <param name="Owner">The step scheduling the <see cref="Child"/> step.</param>
/// <param name="Child">The child <see cref="IStep"/> being scheduled.</param>
/// <param name="CompletionCallback">The <see cref="StepCompletionCallback"/> delegate to invoke when the scheduled <see cref="Child"/> step completes.</param>
/// <param name="Tag">An optional tag.</param>
public record StepCompletionCallbackEntry(StepExecutionContext Owner, StepNode Child, StepCompletionCallback? CompletionCallback, object? Tag = null);

/// <summary>
/// Provides context to the currently executing pipeline.
/// </summary>
[PublicAPI]
public partial class PipelineExecutionContext : IExecutionContext
{
    private static readonly object StepOutputRegistryKey = new();
    private static readonly object LastStepResultKey = new();
    internal static ValueTask Complete(StepExecutionContext context) => context.CompleteStepAsync();
    internal static ValueTask Noop(StepExecutionContext context) => default;
    private readonly IList<StepCompletionCallbackEntry> _completionCallbackEntries = new List<StepCompletionCallbackEntry>();
    private IList<StepExecutionContext> _stepExecutionContexts;
    private readonly IHasher _hasher;
    private readonly ICommitStateHandler _commitStateHandler;

    /// <summary>
    /// Initializes a new instance of <see cref="PipelineExecutionContext"/>.
    /// </summary>
    private PipelineExecutionContext(
        IServiceProvider serviceProvider,
        PipelineGraph pipelineGraph,
        string id,
        string? correlationId,
        string? parentPipelineInstanceId,
        IDictionary<string, object>? input,
        IDictionary<string, object>? properties,
        ExecuteStepDelegate? executeDelegate,
        string? triggerStepId,
        IEnumerable<StepIncident> incidents,
        IEnumerable<Bookmark> originalBookmarks,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        ServiceProvider = serviceProvider;
        SystemClock = serviceProvider.GetRequiredService<ISystemClock>();
        StepRegistry = serviceProvider.GetRequiredService<IStepRegistry>();
        StepRegistryLookup = serviceProvider.GetRequiredService<IStepRegistryLookupService>();
        _hasher = serviceProvider.GetRequiredService<IHasher>();
        _commitStateHandler = serviceProvider.GetRequiredService<ICommitStateHandler>();
        SubStatus = PipelineSubStatus.Pending;
        Id = id;
        CorrelationId = correlationId;
        ParentPipelineInstanceId = parentPipelineInstanceId;
        _stepExecutionContexts = new List<StepExecutionContext>();
        Scheduler = serviceProvider.GetRequiredService<IStepSchedulerFactory>().CreateScheduler();
        IdentityGenerator = serviceProvider.GetRequiredService<IIdentityGenerator>();
        Input = input != null ? new(input, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        Properties = properties != null ? new(properties, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        ExecuteDelegate = executeDelegate;
        TriggerStepId = triggerStepId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        CancellationToken = cancellationToken;
        Incidents = incidents.ToList();
        OriginalBookmarks = originalBookmarks.ToList();
        PipelineGraph = pipelineGraph;
        var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationTokenSources.Add(linkedCancellationTokenSource);
        _cancellationRegistrations.Add(linkedCancellationTokenSource.Token.Register(CancelPipeline));
    }
    
    /// <summary>
    /// Creates a new <see cref="PipelineExecutionContext"/> for the specified pipeline.
    /// </summary>
    public static async Task<PipelineExecutionContext> CreateAsync(
        IServiceProvider serviceProvider,
        PipelineGraph pipelineGraph,
        string id,
        CancellationToken cancellationToken = default)
    {
        var systemClock = serviceProvider.GetRequiredService<ISystemClock>();

        return await CreateAsync(
            serviceProvider,
            pipelineGraph,
            id,
            new List<StepIncident>(),
            new List<Bookmark>(),
            systemClock.UtcNow,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new <see cref="PipelineExecutionContext"/> for the specified pipeline.
    /// </summary>
    public static async Task<PipelineExecutionContext> CreateAsync(
        IServiceProvider serviceProvider,
        PipelineGraph pipelineGraph,
        string id,
        string? correlationId,
        string? parentPipelineInstanceId = null,
        IDictionary<string, object>? input = null,
        IDictionary<string, object>? properties = null,
        ExecuteStepDelegate? executeDelegate = null,
        string? triggerStepId = null,
        CancellationToken cancellationToken = default)
    {
        var systemClock = serviceProvider.GetRequiredService<ISystemClock>();

        return await CreateAsync(
            serviceProvider,
            pipelineGraph,
            id,
            new List<StepIncident>(),
            new List<Bookmark>(),
            systemClock.UtcNow,
            correlationId,
            parentPipelineInstanceId,
            input,
            properties,
            executeDelegate,
            triggerStepId,
            cancellationToken
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new <see cref="PipelineExecutionContext"/> for the specified pipeline.
    /// </summary>
    public static async Task<PipelineExecutionContext> CreateAsync(
        IServiceProvider serviceProvider,
        PipelineGraph pipelineGraph,
        PipelineState pipelineState,
        string? correlationId = null,
        string? parentPipelineInstanceId = null,
        IDictionary<string, object>? input = null,
        IDictionary<string, object>? properties = null,
        ExecuteStepDelegate? executeDelegate = null,
        string? triggerStepId = null,
        CancellationToken cancellationToken = default)
    {
        var pipelineExecutionContext = await CreateAsync(
            serviceProvider,
            pipelineGraph,
            pipelineState.Id,
            pipelineState.Incidents,
            pipelineState.Bookmarks,
            pipelineState.CreatedAt,
            correlationId,
            parentPipelineInstanceId,
            input,
            properties,
            executeDelegate,
            triggerStepId,
            cancellationToken).ConfigureAwait(false);

        var pipelineStateExtractor = serviceProvider.GetRequiredService<IPipelineStateExtractor>();
        await pipelineStateExtractor.ApplyAsync(pipelineExecutionContext, pipelineState).ConfigureAwait(false);

        return pipelineExecutionContext;
    }

    /// <summary>
    /// Creates a new <see cref="PipelineExecutionContext"/> for the specified pipeline.
    /// </summary>
    public static async Task<PipelineExecutionContext> CreateAsync(
        IServiceProvider serviceProvider,
        PipelineGraph pipelineGraph,
        string id,
        IEnumerable<StepIncident> incidents,
        IEnumerable<Bookmark> originalBookmarks,
        DateTimeOffset createdAt,
        string? correlationId = null,
        string? parentPipelineInstanceId = null,
        IDictionary<string, object>? input = null,
        IDictionary<string, object>? properties = null,
        ExecuteStepDelegate? executeDelegate = null,
        string? triggerStepId = null,
        CancellationToken cancellationToken = default)
    {
        // Set up a pipeline execution context.
        var pipelineExecutionContext = new PipelineExecutionContext(
            serviceProvider,
            pipelineGraph,
            id,
            correlationId,
            parentPipelineInstanceId,
            input,
            properties,
            executeDelegate,
            triggerStepId,
            incidents,
            originalBookmarks,
            createdAt,
            cancellationToken)
        {
            MemoryRegister = pipelineGraph.Pipeline.CreateRegister()
        };

        pipelineExecutionContext.ExpressionExecutionContext = new(serviceProvider, pipelineExecutionContext.MemoryRegister, cancellationToken: cancellationToken);

        await pipelineExecutionContext.SetPipelineGraphAsync(pipelineGraph).ConfigureAwait(false);
        return pipelineExecutionContext;
    }

    /// <summary>
    /// Assigns the specified pipeline to this pipeline execution context.
    /// </summary>
    /// <param name="pipelineGraph">The pipeline graph to assign.</param>
    public async Task SetPipelineGraphAsync(PipelineGraph pipelineGraph)
    {
        PipelineGraph = pipelineGraph;
        var nodes = pipelineGraph.Nodes;

        // Register step types.
        var stepTypes = nodes.Select(x => x.Step.GetType()).Distinct().ToList();
        await StepRegistry.RegisterAsync(stepTypes, CancellationToken).ConfigureAwait(false);

        // Update the step execution contexts with the actual step instances.
        foreach (var stepExecutionContext in StepExecutionContexts)
            stepExecutionContext.Step = pipelineGraph.NodeIdLookup[stepExecutionContext.Step.NodeId].Step;
    }

    /// Gets the <see cref="IServiceProvider"/>.
    public IServiceProvider ServiceProvider { get; }

    /// Gets the <see cref="IStepRegistry"/>.
    public IStepRegistry StepRegistry { get; }

    /// Gets the <see cref="IStepRegistryLookupService"/>.
    public IStepRegistryLookupService StepRegistryLookup { get; }

    /// Gets the pipeline graph.
    public PipelineGraph PipelineGraph { get; private set; }

    /// The <see cref="Pipeline"/> associated with the execution context.
    public Pipeline Pipeline => PipelineGraph.Pipeline;

    /// A graph of the pipeline structure.
    public StepNode Graph => PipelineGraph.Root;

    /// The current status of the pipeline.
    public PipelineStatus Status => GetMainStatus(SubStatus);

    /// The current sub status of the pipeline.
    public PipelineSubStatus SubStatus { get; internal set; }

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline instance is actively executing. 
    /// </summary>
    /// <remarks>
    /// This flag is set to <c>true</c> immediately before the pipeline begins execution 
    /// and is set to <c>false</c> once the execution is completed. 
    /// It can be used to determine if a pipeline instance was in-progress in case of unexpected 
    /// application termination, allowing the system to retry execution upon restarting. 
    /// </remarks>
    public bool IsExecuting { get; set; }

    /// The root <see cref="MemoryRegister"/> associated with the execution context.
    public MemoryRegister MemoryRegister { get; private set; } = null!;

    /// A unique ID of the execution context.
    public string Id { get; set; }

    /// <inheritdoc />
    public IStep Step => Pipeline;

    /// An application-specific identifier associated with the execution context.
    public string? CorrelationId { get; set; }

    /// Gets or sets the name of the pipeline instance.
    public string? Name { get; set; }

    /// The ID of the pipeline instance that triggered this instance.
    public string? ParentPipelineInstanceId { get; set; }

    /// The date and time the pipeline execution context was created.
    public DateTimeOffset CreatedAt { get; set; }

    /// The date and time the pipeline execution context was last updated.
    public DateTimeOffset UpdatedAt { get; set; }

    /// The date and time the pipeline execution context has finished.
    public DateTimeOffset? FinishedAt { get; set; }

    /// Gets the clock used to determine the current time.
    public ISystemClock SystemClock { get; }

    /// A flattened list of <see cref="StepNode"/>s from the <see cref="Graph"/>. 
    public IReadOnlyCollection<StepNode> Nodes => PipelineGraph.Nodes.ToList();

    /// A map between step IDs and <see cref="StepNode"/>s in the pipeline graph.
    public IDictionary<string, StepNode> NodeIdLookup => PipelineGraph.NodeIdLookup;

    /// A map between hashed step node IDs and <see cref="StepNode"/>s in the pipeline graph.
    public IDictionary<string, StepNode> NodeHashLookup => PipelineGraph.NodeHashLookup;

    /// A map between <see cref="IStep"/>s and <see cref="StepNode"/>s in the pipeline graph.
    public IDictionary<IStep, StepNode> NodeStepLookup => PipelineGraph.NodeStepLookup;

    /// The <see cref="IStepScheduler"/> for the execution context.
    public IStepScheduler Scheduler { get; }

    /// Gets the <see cref="IIdentityGenerator"/>.
    public IIdentityGenerator IdentityGenerator { get; }

    /// Gets the collection of original bookmarks associated with the pipeline execution context.
    public ICollection<Bookmark> OriginalBookmarks { get; set; }

    /// A collection of collected bookmarks during pipeline execution. 
    public ICollection<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();

    /// A diff between the original bookmarks and the current bookmarks.
    public Diff<Bookmark> BookmarksDiff => Diff.For(OriginalBookmarks, Bookmarks);

    /// <summary>
    /// A dictionary of inputs provided at the start of the current pipeline execution.
    /// </summary>
    public IDictionary<string, object> Input { get; set; }

    /// <summary>
    /// A dictionary of outputs provided by the current pipeline execution.
    /// </summary>
    public IDictionary<string, object> Output { get; set; } = new Dictionary<string, object>();

    /// <inheritdoc />
    public IDictionary<string, object> Properties { get; set; }

    /// <summary>
    /// A dictionary that can be used by application code and middleware to store information and even services. Values do not need to be serializable.
    /// All data will be gone once pipeline execution completes.
    /// </summary>
    public IDictionary<object, object> TransientProperties { get; set; } = new Dictionary<object, object>();

    /// <summary>
    /// A collection of incidents that may have occurred during execution.
    /// </summary>
    public ICollection<StepIncident> Incidents { get; set; }

    /// <summary>
    /// The current <see cref="ExecuteStepDelegate"/> delegate to invoke when executing the next step.
    /// </summary>
    public ExecuteStepDelegate? ExecuteDelegate { get; set; }

    /// <summary>
    /// Provides context about the bookmark that was used to resume pipeline execution, if any.
    /// </summary>
    public ResumedBookmarkContext? ResumedBookmarkContext { get; set; }

    /// <summary>
    /// The ID of the step associated with the trigger that caused this pipeline execution, if any.
    /// </summary>
    public string? TriggerStepId { get; set; }

    /// <summary>
    /// A set of cancellation tokens that can be used to cancel the pipeline execution without cancelling system-level operations.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// A list of <see cref="StepCompletionCallbackEntry"/> callbacks that are invoked when the associated child step completes.
    /// </summary>
    public ICollection<StepCompletionCallbackEntry> CompletionCallbacks => new ReadOnlyCollection<StepCompletionCallbackEntry>(_completionCallbackEntries);

    /// <summary>
    /// A list of <see cref="StepExecutionContext"/>s that are currently active.
    /// </summary>
    public IReadOnlyCollection<StepExecutionContext> StepExecutionContexts
    {
        get => _stepExecutionContexts.AsReadOnly();
        internal set => _stepExecutionContexts = value.ToList();
    }

    /// <summary>
    /// The last execution log sequence number. This number is incremented every time a new entry is added to the execution log and is persisted alongside the pipeline instance and restored when the pipeline is resumed.
    /// </summary>
    public long ExecutionLogSequence { get; set; }

    /// <summary>
    /// A collection of execution log entries. This collection is flushed when the pipeline execution context ends.
    /// </summary>
    public ICollection<PipelineExecutionLogEntry> ExecutionLog { get; } = new List<PipelineExecutionLogEntry>();

    /// <summary>
    /// The expression execution context for the current pipeline execution.
    /// </summary>
    public ExpressionExecutionContext ExpressionExecutionContext { get; private set; } = null!;

    /// <inheritdoc />
    public IEnumerable<Variable> Variables => Pipeline.Variables;

    /// <summary>
    /// Resolves the specified service type from the service provider.
    /// </summary>
    public T GetRequiredService<T>() where T : notnull => ServiceProvider.GetRequiredService<T>();

    /// <summary>
    /// Resolves the specified service type from the service provider.
    /// </summary>
    public object GetRequiredService(Type serviceType) => ServiceProvider.GetRequiredService(serviceType);

    /// <summary>
    /// Resolves the specified service type from the service provider, or creates a new instance if the service type was not found in the service container.
    /// </summary>
    public T GetOrCreateService<T>() where T : notnull => ActivatorUtilities.GetServiceOrCreateInstance<T>(ServiceProvider);

    /// <summary>
    /// Resolves the specified service type from the service provider, or creates a new instance if the service type was not found in the service container.
    /// </summary>
    public object GetOrCreateService(Type serviceType) => ActivatorUtilities.GetServiceOrCreateInstance(ServiceProvider, serviceType);

    /// <summary>
    /// Resolves the specified service type from the service provider.
    /// </summary>
    public T? GetService<T>() where T : notnull => ServiceProvider.GetService<T>();

    /// <summary>
    /// Resolves the specified service type from the service provider.
    /// </summary>
    public object? GetService(Type serviceType) => ServiceProvider.GetService(serviceType);

    /// <summary>
    /// Resolves multiple implementations of the specified service type from the service provider.
    /// </summary>
    public IEnumerable<T> GetServices<T>() where T : notnull => ServiceProvider.GetServices<T>();

    /// <summary>
    /// Registers a completion callback for the specified step.
    /// </summary>
    public void AddCompletionCallback(StepExecutionContext owner, StepNode child, StepCompletionCallback? completionCallback = null, object? tag = null)
    {
        var entry = new StepCompletionCallbackEntry(owner, child, completionCallback, tag);
        _completionCallbackEntries.Add(entry);
    }

    /// <summary>
    /// Unregisters the completion callback for the specified owner and child step.
    /// </summary>
    public StepCompletionCallbackEntry? PopCompletionCallback(StepExecutionContext owner, StepNode child)
    {
        var entry = _completionCallbackEntries.FirstOrDefault(x => x.Owner == owner && x.Child == child);

        if (entry == null)
            return null;

        RemoveCompletionCallback(entry);
        return entry;
    }

    public void RemoveCompletionCallback(StepCompletionCallbackEntry entry) => _completionCallbackEntries.Remove(entry);

    public void RemoveCompletionCallbacks(IEnumerable<StepCompletionCallbackEntry> entries)
    {
        foreach (var entry in entries.ToList())
            _completionCallbackEntries.Remove(entry);
    }

    /// <summary>
    /// Clears all step completion callback entries from the pipeline execution context.
    /// </summary>
    public void ClearCompletionCallbacks()
    {
        _completionCallbackEntries.Clear();
    }

    /// <summary>
    /// Finds the step based on the provided <paramref name="handle"/>.
    /// </summary>
    /// <param name="handle">The handle containing the identification parameters for the step.</param>
    /// <returns>The step found based on the handle, or null if no step is found.</returns>
    public IStep? FindStep(StepHandle handle)
    {
        return handle.StepId != null
            ? FindStepById(handle.StepId)
            : handle.StepNodeId != null
                ? FindStepByNodeId(handle.StepNodeId)
                : handle.StepInstanceId != null
                    ? FindStepByInstanceId(handle.StepInstanceId)
                    : handle.StepHash != null
                        ? FindStepByHash(handle.StepHash)
                        : null;
    }

    /// <summary>
    /// Returns the <see cref="StepNode"/> with the specified step ID from the pipeline graph.
    /// </summary>
    public StepNode? FindNodeById(string nodeId) => NodeIdLookup.TryGetValue(nodeId, out var node) ? node : null;

    /// <summary>
    /// Returns the <see cref="StepNode"/> with the specified hash of the step node ID from the pipeline graph.
    /// </summary>
    /// <param name="hash">The hash of the step node ID.</param>
    /// <returns>The <see cref="StepNode"/> with the specified hash of the step node ID.</returns>
    public StepNode? FindNodeByHash(string hash) => NodeHashLookup.TryGetValue(hash, out var node) ? node : null;

    /// Returns the <see cref="StepNode"/> containing the specified step from the pipeline graph.
    public StepNode? FindNodeByStep(IStep step)
    {
        return NodeStepLookup.TryGetValue(step, out var node) ? node : null;
    }

    /// Returns the <see cref="StepNode"/> associated with the specified step ID.
    public StepNode? FindNodeByStepId(string stepId) => Nodes.FirstOrDefault(x => x.Step.Id == stepId);

    /// Returns the <see cref="IStep"/> with the specified ID from the pipeline graph.
    public IStep? FindStepByNodeId(string nodeId) => FindNodeById(nodeId)?.Step;

    /// Returns the <see cref="IStep"/> with the specified ID from the pipeline graph.
    public IStep? FindStepById(string stepId) => FindNodeById(NodeIdLookup.SingleOrDefault(n => n.Key.EndsWith(stepId)).Value.NodeId)?.Step;

    /// Returns the <see cref="IStep"/> with the specified hash of the step node ID from the pipeline graph.
    /// <param name="hash">The hash of the step node ID.</param>
    /// <returns>The <see cref="IStep"/> with the specified hash of the step node ID.</returns>
    public IStep? FindStepByHash(string hash) => FindNodeByHash(hash)?.Step;

    /// Returns the <see cref="StepExecutionContext"/> with the specified step instance ID.
    public IStep? FindStepByInstanceId(string stepInstanceId) => StepExecutionContexts.FirstOrDefault(x => x.Id == stepInstanceId)?.Step;

    /// Returns a custom property with the specified key from the <see cref="Properties"/> dictionary.
    public T? GetProperty<T>(string key) => Properties.TryGetValue(key, out var value) ? value.ConvertTo<T>() : default;

    /// Sets a custom property with the specified key on the <see cref="Properties"/> dictionary.
    public void SetProperty<T>(string key, T value) => Properties[key] = value!;

    /// Updates a custom property with the specified key on the <see cref="Properties"/> dictionary.
    public T UpdateProperty<T>(string key, Func<T?, T> updater)
    {
        var value = GetProperty<T?>(key);
        value = updater(value);
        Properties[key] = value!;
        return value;
    }

    /// Returns true if the <see cref="Properties"/> dictionary contains the specified key.
    public bool HasProperty(string name) => Properties.ContainsKey(name);

    internal bool CanTransitionTo(PipelineSubStatus targetSubStatus) => ValidateStatusTransition();

    internal void TransitionTo(PipelineSubStatus subStatus)
    {
        if (!ValidateStatusTransition())
            throw new($"Cannot transition from {SubStatus} to {subStatus}");

        SubStatus = subStatus;
        UpdatedAt = SystemClock.UtcNow;

        if (Status == PipelineStatus.Finished)
            FinishedAt = UpdatedAt;

        if (Status == PipelineStatus.Finished || SubStatus == PipelineSubStatus.Suspended)
        {
            foreach (var registration in _cancellationRegistrations)
                registration.Dispose();
        }
    }

    /// Creates a new <see cref="StepExecutionContext"/> for the specified step.
    public async Task<StepExecutionContext> CreateStepExecutionContextAsync(IStep step, StepInvocationOptions? options = null)
    {
        var stepDescriptor = await StepRegistryLookup.FindAsync(step).ConfigureAwait(false) ?? throw new StepNotFoundException(step.Type);
        var tag = options?.Tag;
        var parentContext = options?.Owner;
        var now = SystemClock.UtcNow;
        var id = IdentityGenerator.GenerateId();
        var stepExecutionContext = new StepExecutionContext(id, this, parentContext, step, stepDescriptor, now, tag, SystemClock, CancellationToken);
        var variablesToDeclare = options?.Variables ?? [];
        var variableContainer = new[]
        {
            stepExecutionContext.StepNode
        }.Concat(stepExecutionContext.StepNode.Ancestors()).FirstOrDefault(x => x.Step is IVariableContainer)?.Step as IVariableContainer;
        stepExecutionContext.ExpressionExecutionContext.TransientProperties[ExpressionExecutionContextExtensions.StepExecutionContextKey] = stepExecutionContext;

        if (variableContainer != null)
        {
            foreach (var variable in variablesToDeclare)
            {
                // Declare a dynamic variable on the step execution context.
                stepExecutionContext.DynamicVariables.RemoveWhere(x => x.Name == variable.Name);
                stepExecutionContext.DynamicVariables.Add(variable);

                // Assign the variable to the expression execution context.
                stepExecutionContext.ExpressionExecutionContext.CreateVariable(variable.Name, variable.Value);
            }
        }

        var stepInput = options?.Input ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        stepExecutionContext.StepInput.Merge(stepInput);

        // Populate call stack fields from options
        stepExecutionContext.SchedulingStepExecutionId = options?.SchedulingStepExecutionId;
        stepExecutionContext.SchedulingPipelineInstanceId = options?.SchedulingPipelineInstanceId;

        // Calculate call stack depth
        if (options?.SchedulingStepExecutionId != null)
        {
            // First, try to find the scheduling context in the current pipeline
            var schedulingContext = StepExecutionContexts.FirstOrDefault(x => x.Id == options.SchedulingStepExecutionId);
            if (schedulingContext != null)
            {
                // Found in current pipeline - use its depth
                stepExecutionContext.SchedulingStepId = schedulingContext.Step.Id;
                stepExecutionContext.CallStackDepth = schedulingContext.CallStackDepth + 1;
            }
            else if (options.SchedulingCallStackDepth.HasValue)
            {
                // Not found but caller provided depth (e.g., cross-pipeline invocation)
                stepExecutionContext.CallStackDepth = options.SchedulingCallStackDepth.Value + 1;
            }
            // else: scheduling context not found and no depth provided.
            // Depth stays at default (0), which may result in incorrect call stack depth tracking
            // if the scheduling context should have been present but wasn't found.
        }

        return stepExecutionContext;
    }

    /// Returns a register of recorded step output.
    public StepOutputRegister GetStepOutputRegister() => TransientProperties.GetOrAdd(StepOutputRegistryKey, () => new StepOutputRegister());

    /// Returns the last step result.
    public object? GetLastStepResult() => TransientProperties.TryGetValue(LastStepResultKey, out var value) ? value : null;

    /// Adds the specified <see cref="StepExecutionContext"/> to the pipeline execution context.
    public void AddStepExecutionContext(StepExecutionContext context) => _stepExecutionContexts.Add(context);

    /// Removes the specified <see cref="StepExecutionContext"/> from the pipeline execution context.
    public void RemoveStepExecutionContext(StepExecutionContext context)
    {
        _stepExecutionContexts.Remove(context);
        context.ParentStepExecutionContext?.Children.Remove(context);
    }

    /// Removes the specified <see cref="StepExecutionContext"/> from the pipeline execution context.
    /// <param name="predicate">The predicate used to filter the step execution contexts to remove.</param>
    public void RemoveStepExecutionContexts(Func<StepExecutionContext, bool> predicate)
    {
        var itemsToRemove = _stepExecutionContexts.Where(predicate).ToList();
        foreach (var item in itemsToRemove)
            RemoveStepExecutionContext(item);
    }

    /// <summary>
    /// Removes all completed step execution contexts that have a parent step execution context.
    /// </summary>
    public void ClearCompletedStepExecutionContexts()
    {
        RemoveStepExecutionContexts(x => x is { IsCompleted: true, ParentStepExecutionContext: not null });
    }

    public IEnumerable<StepExecutionContext> GetActiveStepExecutionContexts()
    {
        // Filter out completed step execution contexts, except for the root Pipeline step context, which stores pipeline-level variables.
        // This will currently break scripts accessing step output directly, but there's a workaround for that via variable capturing.
        // We may ultimately restore direct output access, but differently.
        return StepExecutionContexts.Where(x => !x.IsCompleted || x.ParentStepExecutionContext == null);
    }

    /// <summary>
    /// Records the output of the specified step into the current pipeline execution context.
    /// </summary>
    /// <param name="stepExecutionContext">The <see cref="StepExecutionContext"/> of the step.</param>
    /// <param name="outputName">The name of the output.</param>
    /// <param name="value">The value of the output.</param>
    internal void RecordStepOutput(StepExecutionContext stepExecutionContext, string? outputName, object? value)
    {
        var register = GetStepOutputRegister();
        register.Record(stepExecutionContext, outputName, value);

        // If the output name is the default output name, record the value as the last step result.
        if (outputName == StepOutputRegister.DefaultOutputName)
            TransientProperties[LastStepResultKey] = value!;
    }

    private PipelineStatus GetMainStatus(PipelineSubStatus subStatus) =>
        subStatus switch
        {
            PipelineSubStatus.Pending => PipelineStatus.Running,
            PipelineSubStatus.Cancelled => PipelineStatus.Finished,
            PipelineSubStatus.Executing => PipelineStatus.Running,
            PipelineSubStatus.Faulted => PipelineStatus.Finished,
            PipelineSubStatus.Finished => PipelineStatus.Finished,
            PipelineSubStatus.Suspended => PipelineStatus.Running,
            _ => throw new ArgumentOutOfRangeException(nameof(subStatus), subStatus, null)
        };

    // TODO: Check if we should not use the target subStatus here instead.
    private bool ValidateStatusTransition()
    {
        var currentMainStatus = GetMainStatus(SubStatus);
        return currentMainStatus != PipelineStatus.Finished;
    }

    public Task CommitAsync()
    {
        return _commitStateHandler.CommitAsync(this, CancellationToken);
    }
}

