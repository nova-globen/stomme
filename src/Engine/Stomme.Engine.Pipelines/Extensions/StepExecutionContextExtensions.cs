using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Stomme.Engine.Common;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.Signals;
using Stomme.Engine.Pipelines.State;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extension methods for <see cref="StepExecutionContext"/>.
/// </summary>
[PublicAPI]
public static partial class StepExecutionContextExtensions
{
    private const string ExtensionsMetadataKey = "Extensions";

    /// <param name="context">The <see cref="StepExecutionContext"/></param>
    extension(StepExecutionContext context)
    {
        /// <summary>
        /// Attempts to get a value from the input provided via <see cref="PipelineExecutionContext"/>. If a value was found, an attempt is made to convert it into the specified type <code>T</code>.
        /// </summary>
        public bool TryGetPipelineInput<T>(string key, out T value, JsonSerializerOptions? serializerOptions = null)
        {
            var wellKnownTypeRegistry = context.GetRequiredService<IWellKnownTypeRegistry>();

            if (context.PipelineInput.TryGetValue(key, out var v))
            {
                value = v.ConvertTo<T>(new(serializerOptions, wellKnownTypeRegistry))!;
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Gets a value from the input provided via <see cref="PipelineExecutionContext"/>. If a value was found, an attempt is made to convert it into the specified type <code>T</code>.
        /// </summary>
        public T GetPipelineInput<T>(JsonSerializerOptions? serializerOptions = null) => context.GetPipelineInput<T>(typeof(T).Name, serializerOptions);

        /// <summary>
        /// Gets a value from the input provided via <see cref="PipelineExecutionContext"/>. If a value was found, an attempt is made to convert it into the specified type <code>T</code>.
        /// </summary>
        public T GetPipelineInput<T>(string key, JsonSerializerOptions? serializerOptions = null)
        {
            var wellKnownTypeRegistry = context.GetRequiredService<IWellKnownTypeRegistry>();
            return context.PipelineInput[key].ConvertTo<T>(new(serializerOptions, wellKnownTypeRegistry))!;
        }

        /// <summary>
        /// Sets the Result property of the specified step.
        /// </summary>
        /// being extended.
        /// <param name="value">The value to set.</param>
        /// <exception cref="Exception">Thrown when the specified step does not implement <see cref="IStepWithResult"/>.</exception>
        public void SetResult(object? value)
        {
            var step = context.Step as IStepWithResult ?? throw new($"Cannot set result on step {context.Step.Id} because it does not implement {nameof(IStepWithResult)}.");
            context.Set(step.Result, value, "Result");
        }

        /// <summary>
        /// Returns true if this step is triggered for the first time and not being resumed.
        /// </summary>
        public bool IsTriggerOfPipeline() => context.PipelineExecutionContext.TriggerStepId == context.Step.Id;

        /// <summary>
        /// Creates a pipeline variable by name and optionally sets the value.
        /// </summary>
        /// <param name="name">The name of the variable.</param>
        /// <param name="value">The value of the variable.</param>
        /// <param name="storageDriverType">The type of storage driver to use for the variable.</param>
        /// <param name="configure">A callback to configure the memory block.</param>
        /// <returns>The created <see cref="Variable"/>.</returns>
        public Variable CreateVariable(string name, object? value, Type? storageDriverType = null, Action<MemoryBlock>? configure = null)
        {
            return context.ExpressionExecutionContext.CreateVariable(name, value, storageDriverType, configure);
        }

        /// <summary>
        /// Sets a pipeline variable by name.
        /// </summary>
        /// <param name="name">The name of the variable.</param>
        /// <param name="value">The value of the variable.</param>
        /// <param name="configure">A callback to configure the memory block.</param>
        /// <returns>The created <see cref="Variable"/>.</returns>
        public Variable SetVariable(string name, object? value, Action<MemoryBlock>? configure = null) =>
            context.ExpressionExecutionContext.SetVariable(name, value, configure);

        public Variable SetDynamicVariable<T>(string name, T value, Action<MemoryBlock>? configure = null)
        {
            // Check if a predefined variable already exists.
            var predefinedVariable = context.ExpressionExecutionContext.GetVariable(name);

            if (predefinedVariable != null)
            {
                context.SetVariable(name, value);
                return predefinedVariable;
            }

            // No predefined variable exists, so we will add a dynamic variable to the current container in scope.
            var container = context.FindParentWithVariableContainer();

            if (container == null)
                throw new("No parent variable container found");

            var existingVariable = container.DynamicVariables.FirstOrDefault(x => x.Name == name);

            if (existingVariable == null)
                container.DynamicVariables.Add(new Variable<T>(name, value));

            return context.SetVariable(name, value);
        }

        /// <summary>
        /// Gets a pipeline variable by name.
        /// </summary>
        /// <param name="name">The name of the variable.</param>
        /// <typeparam name="T">The type of the variable.</typeparam>
        /// <returns>The variable if found, otherwise null.</returns>
        public T? GetVariable<T>(string name) => context.ExpressionExecutionContext.GetVariable<T?>(name);

        /// <summary>
        /// Returns a dictionary of variable keys and their values across scopes.
        /// </summary>
        public IDictionary<string, object> GetVariableValues() => context.ExpressionExecutionContext.ReadAndFlattenMemoryBlocks();

        /// <summary>
        /// Returns a set of tuples containing the step and its descriptor for all steps with outputs.
        /// </summary>
        public async IAsyncEnumerable<(IStep Step, StepDescriptor StepDescriptor)> GetStepsWithOutputs()
        {
            // Get current container.
            var currentContainerNode = context.FindParentWithVariableContainer()?.StepNode;

            if (currentContainerNode == null)
                yield break;

            // Get all nodes in the current container
            var pipelineExecutionContext = context.PipelineExecutionContext;
            var containedNodes = pipelineExecutionContext.Nodes.Where(x => x.Parents.Contains(currentContainerNode)).Distinct().ToList();

            // Select steps with outputs.
            var stepRegistry = pipelineExecutionContext.GetRequiredService<IStepRegistryLookupService>();
            var stepsWithOutputs = containedNodes.GetStepsWithOutputs(stepRegistry);

            await foreach (var (step, stepDescriptor) in stepsWithOutputs.ConfigureAwait(false))
                yield return (step, stepDescriptor);
        }
    }

    /// <summary>
    /// Returns a set of tuples containing the step and its descriptor for all steps with outputs.
    /// </summary>
    public static async IAsyncEnumerable<(IStep step, StepDescriptor stepDescriptor)> GetStepsWithOutputs(this IEnumerable<StepNode> nodes, IStepRegistryLookupService stepRegistryLookup)
    {
        foreach (var node in nodes)
        {
            var step = node.Step;
            var stepDescriptor = await stepRegistryLookup.FindAsync(step.Type, step.Version).ConfigureAwait(false);
            if (stepDescriptor != null && stepDescriptor.Outputs.Any())
                yield return (step, stepDescriptor);
        }
    }

    /// <param name="stepExecutionContext">The <see cref="StepExecutionContext"/> being extended.</param>
    extension(StepExecutionContext stepExecutionContext)
    {
        /// <summary>
        /// Returns the step with the specified ID or name.
        /// </summary>
        public IStep? FindStepByIdOrName(string idOrName)
        {
            // Get current container.
            var currentContainerNode = stepExecutionContext.FindParentWithVariableContainer()?.StepNode;

            if (currentContainerNode == null)
                return null;

            // Get all nodes in the current container
            var pipelineExecutionContext = stepExecutionContext.PipelineExecutionContext;
            var containedNodes = pipelineExecutionContext.Nodes.Where(x => x.Parents.Contains(currentContainerNode)).Distinct().ToList();
            var node = containedNodes.FirstOrDefault(x => x.Step.Name == idOrName || x.Step.Id == idOrName);
            return node?.Step;
        }

        /// <summary>
        /// Returns the outcome name for the specified port property name.
        /// </summary>
        /// <param name="portPropertyName">The name of the port property.</param>
        /// <returns>The outcome name.</returns>
        public string GetOutcomeName(string portPropertyName)
        {
            var owner = stepExecutionContext.Step;
            var ports = owner.GetType().GetProperties().Where(x => typeof(IStep).IsAssignableFrom(x.PropertyType)).ToList();

            var portQuery =
                from p in ports
                where p.Name == portPropertyName
                select p;

            var portProperty = portQuery.First();
            return portProperty.GetCustomAttribute<PortAttribute>()?.Name ?? portProperty.Name;
        }

        /// <summary>
        /// Send a signal up the current hierarchy of ancestors.
        /// </summary>
        public async ValueTask SendSignalAsync(object signal)
        {
            var receivingContexts = new[]
            {
                stepExecutionContext
            }.Concat(stepExecutionContext.GetAncestors()).ToList();
            var logger = stepExecutionContext.GetRequiredService<ILogger<StepExecutionContext>>();
            var signalType = signal.GetType();
            var signalTypeName = signalType.Name;

            // Let all ancestors receive the signal.
            foreach (var ancestorContext in receivingContexts)
            {
                var signalContext = new SignalContext(ancestorContext, stepExecutionContext, stepExecutionContext.CancellationToken);

                if (ancestorContext.Step is not ISignalHandler handler)
                    continue;

                logger.LogDebug("Receiving signal {SignalType} on step {StepId} of type {StepType}", signalTypeName, ancestorContext.Step.Id, ancestorContext.Step.Type);
                await handler.ReceiveSignalAsync(signal, signalContext).ConfigureAwait(false);

                if (signalContext.StopPropagationRequested)
                {
                    logger.LogDebug("Propagation of signal {SignalType} on step {StepId} of type {StepType} was stopped", signalTypeName, ancestorContext.Step.Id, ancestorContext.Step.Type);
                    return;
                }
            }
        }

        /// <summary>
        /// Complete the current step. This should only be called by steps that explicitly suppress automatic-completion.
        /// </summary>
        public async ValueTask ScheduleOutcomesAsync(params string[] outcomes)
        {
            // Record the outcomes, if any.
            stepExecutionContext.JournalData["Outcomes"] = outcomes;

            // Record the output, if any.
            var step = stepExecutionContext.Step;
            var expressionExecutionContext = stepExecutionContext.ExpressionExecutionContext;
            var stepDescriptor = stepExecutionContext.StepDescriptor;
            var outputDescriptors = stepDescriptor.Outputs;
            var outputs = outputDescriptors.ToDictionary(x => x.Name, x => step.GetOutput(expressionExecutionContext, x.Name)!);
            var serializer = stepExecutionContext.GetRequiredService<ISafeSerializer>();

            foreach (var output in outputs)
            {
                var outputName = output.Key;
                var outputValue = output.Value;

                if (outputValue == null!)
                    continue;

                var serializedOutputValue = serializer.Serialize(outputValue);
                stepExecutionContext.JournalData[outputName] = serializedOutputValue;
            }

            // Send a signal.
            await stepExecutionContext.SendSignalAsync(new ScheduleStepOutcomes(outcomes)).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancel the step. For blocking steps, it means their bookmarks will be removed. For job steps, the background work will be cancelled.
        /// </summary>
        public async Task CancelStepAsync()
        {
            // If the step is not running, do nothing.
            if (stepExecutionContext.Status != StepStatus.Running && stepExecutionContext.Status != StepStatus.Faulted)
                return;

            // Select all child contexts.
            var childContexts = stepExecutionContext.Children.ToList();

            foreach (var childContext in childContexts)
                await childContext.CancelStepAsync().ConfigureAwait(false);

            var publisher = stepExecutionContext.GetRequiredService<INotificationSender>();
            stepExecutionContext.TransitionTo(StepStatus.Canceled);
            stepExecutionContext.ClearBookmarks();
            stepExecutionContext.ClearCompletionCallbacks();
            stepExecutionContext.PipelineExecutionContext.Bookmarks.RemoveWhere(x => x.StepNodeId == stepExecutionContext.NodeId);

            // Add an execution log entry.
            stepExecutionContext.AddExecutionLogEntry("Canceled");

            await stepExecutionContext.SendSignalAsync(new CancelSignal()).ConfigureAwait(false);
            await publisher.SendAsync(new StepCancelled(stepExecutionContext)).ConfigureAwait(false);
        }

        /// <summary>
        /// Marks the current step as faulted, records an exception, and transitions its status to <see cref="StepStatus.Faulted"/>.
        /// An incident is logged, and the fault count for the step and its ancestor steps is incremented.
        /// </summary>
        /// <param name="e">The exception to be recorded as the cause of the fault.</param>
        public void Fault(Exception e)
        {
            stepExecutionContext.Exception = e;
            stepExecutionContext.TransitionTo(StepStatus.Faulted);
            var step = stepExecutionContext.Step;
            var exceptionState = ExceptionState.FromException(e);
            var systemClock = stepExecutionContext.GetRequiredService<ISystemClock>();
            var now = systemClock.UtcNow;
            var incident = new StepIncident(step.Id, step.NodeId, step.Type, e.Message, exceptionState, now);
            stepExecutionContext.PipelineExecutionContext.Incidents.Add(incident);
            stepExecutionContext.AggregateFaultCount++;
        
            var ancestors = stepExecutionContext.GetAncestors();

            foreach (var ancestor in ancestors)
                ancestor.AggregateFaultCount++;
        }

        /// <summary>
        /// Recovers the current step from a faulted state by resetting fault counts and transitioning the step to the running status.
        /// </summary>
        public void RecoverFromFault()
        {
            stepExecutionContext.AggregateFaultCount = 0;

            var ancestors = stepExecutionContext.GetAncestors();

            foreach (var ancestor in ancestors)
                ancestor.AggregateFaultCount--;

            stepExecutionContext.TransitionTo(StepStatus.Running);
        }

        /// <summary>
        /// Returns the first context that is associated with a <see cref="IVariableContainer"/>.
        /// </summary>
        public StepExecutionContext? FindParentWithVariableContainer()
        {
            return stepExecutionContext.FindParent(x => x.Step is IVariableContainer);
        }

        /// <summary>
        /// Returns the first context in the hierarchy that matches the specified predicate.
        /// </summary>
        /// <param name="predicate">The predicate to match.</param>
        /// <returns>The first context that matches the predicate or <c>null</c> if no match was found.</returns>
        public StepExecutionContext? FindParent(Func<StepExecutionContext, bool> predicate)
        {
            var currentContext = stepExecutionContext;

            while (currentContext != null)
            {
                if (predicate(currentContext))
                    return currentContext;

                currentContext = currentContext.ParentStepExecutionContext;
            }

            return null;
        }

        /// <summary>
        /// Returns a flattened list of the current context's ancestors.
        /// </summary>
        public IEnumerable<StepExecutionContext> GetAncestors()
        {
            var current = stepExecutionContext.ParentStepExecutionContext;

            while (current != null)
            {
                yield return current;
                current = current.ParentStepExecutionContext;
            }
        }

        /// <summary>
        /// Returns a flattened list of the current context's descendants.
        /// </summary>
        public IEnumerable<StepExecutionContext> GetDescendents()
        {
            var children = stepExecutionContext.Children.ToList();

            foreach (var child in children)
            {
                yield return child;

                foreach (var descendent in child.GetDescendents())
                    yield return descendent;
            }
        }

        /// <summary>
        /// Returns a flattened list of the current context's immediate active children.
        /// </summary>
        public IEnumerable<StepExecutionContext> GetActiveChildren()
        {
            return stepExecutionContext.Children;
        }

        /// <summary>
        /// Returns a flattened list of the current context's immediate children.
        /// </summary>
        public IEnumerable<StepExecutionContext> GetChildren()
        {
            return stepExecutionContext.Children;
        }

        /// <summary>
        /// Returns a flattened list of the current context's descendants.
        /// </summary>
        public IEnumerable<StepExecutionContext> GetDescendants()
        {
            var children = stepExecutionContext.Children.ToList();

            foreach (var child in children)
            {
                yield return child;

                foreach (var descendant in child.GetDescendants())
                    yield return descendant;
            }
        }

        /// <summary>
        /// Sets extension data in the metadata. Represents specific data that is exposed generically for a step.
        /// </summary>
        public void SetExtensionsMetadata(string key, object? value)
        {
            var extensionsDictionary = stepExecutionContext.GetExtensionsMetadata() ?? new Dictionary<string, object?>();

            extensionsDictionary[key] = value;
        
            stepExecutionContext.Metadata[ExtensionsMetadataKey] = extensionsDictionary;
        }

        /// <summary>
        /// Retrieves the extension data from the metadata. Represents specific data that is exposed generically for a step.
        /// </summary>
        public Dictionary<string, object?>? GetExtensionsMetadata()
        {
            return stepExecutionContext.Metadata.TryGetValue(ExtensionsMetadataKey, out var value) ? value as Dictionary<string, object?> : null;
        }

        /// <summary>
        /// Gets the output of a step even when the context is not the same as the step's context.
        /// This is useful when you want to get the output of a previously executed step.
        /// </summary>
        /// <param name="property"></param>
        /// <typeparam name="TProp"></typeparam>
        /// <returns></returns>
        public object? GetStepOutput<TProp>(Expression<Func<TProp>> property)
        {
            if (property.Body is not MemberExpression memberExpr)
            {
                return null;
            }
        
            var propertyName = memberExpr.Member.Name;
            var value = stepExecutionContext.Get(propertyName);
        
            if (value != null)
                return value;

            var registry = stepExecutionContext.PipelineExecutionContext.GetStepOutputRegister();
            
            return registry.FindOutputByStepInstanceId(stepExecutionContext.Id, propertyName);
        }

        public bool GetHasEvaluatedProperties() => stepExecutionContext.TransientProperties.TryGetValue<bool>("HasEvaluatedProperties", out var value) && value;
        public void SetHasEvaluatedProperties() => stepExecutionContext.TransientProperties["HasEvaluatedProperties"] = true;

        /// <summary>
        /// Retrieves the complete execution chain for this step execution context by traversing the SchedulingStepExecutionId chain.
        /// Returns contexts ordered from root (depth 0) to this context.
        /// </summary>
        /// <returns>A collection of step execution contexts representing the complete call stack, ordered from root to leaf.</returns>
        public IEnumerable<StepExecutionContext> GetExecutionChain()
        {
            var chain = new List<StepExecutionContext>();
            var visited = new HashSet<string>();
            var currentContext = stepExecutionContext;

            // Traverse the chain backwards from this context to the root
            while (currentContext != null && visited.Add(currentContext.Id))
            {
                chain.Add(currentContext);

                // Find the parent in the pipeline execution context
                if (currentContext.SchedulingStepExecutionId != null)
                {
                    currentContext = currentContext.PipelineExecutionContext.StepExecutionContexts
                        .FirstOrDefault(x => x.Id == currentContext.SchedulingStepExecutionId);
                }
                else
                {
                    break;
                }
            }

            // Reverse to get root-to-leaf order
            chain.Reverse();
            return chain;
        }
    }
}