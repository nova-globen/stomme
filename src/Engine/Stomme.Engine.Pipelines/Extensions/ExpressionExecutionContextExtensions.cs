using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Common;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Humanizer;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extensions on <see cref="ExpressionExecutionContext"/>
/// </summary>
public static class ExpressionExecutionContextExtensions
{
    /// <summary>
    /// The key used to store the <see cref="PipelineExecutionContext"/> in the <see cref="ExpressionExecutionContext.TransientProperties"/> dictionary.
    /// </summary>
    public static readonly object PipelineExecutionContextKey = new();

    /// <summary>
    /// The key used to store the <see cref="StepExecutionContext"/> in the <see cref="ExpressionExecutionContext.TransientProperties"/> dictionary.
    /// </summary>
    public static readonly object StepExecutionContextKey = new();

    /// <summary>
    /// The key used to store the input in the <see cref="ExpressionExecutionContext.TransientProperties"/> dictionary.
    /// </summary>
    public static readonly object InputKey = new();

    /// <summary>
    /// The key used to store the pipeline in the <see cref="ExpressionExecutionContext.TransientProperties"/> dictionary.
    /// </summary>
    public static readonly object PipelineKey = new();
    
    /// <summary>
    /// The key used to store the step in the <see cref="ExpressionExecutionContext.TransientProperties"/> dictionary.
    /// </summary>
    public static readonly object StepKey = new();

    /// <summary>
    /// Creates a dictionary for the specified <see cref="PipelineExecutionContext"/> and <see cref="StepExecutionContext"/>.
    /// </summary>
    public static IDictionary<object, object> CreateStepExecutionContextPropertiesFrom(PipelineExecutionContext pipelineExecutionContext, IDictionary<string, object> input) =>
        new Dictionary<object, object>
        {
            [PipelineExecutionContextKey] = pipelineExecutionContext,
            [InputKey] = input,
            [PipelineKey] = pipelineExecutionContext.Pipeline,
        };

    /// <summary>
    /// Creates a dictionary for the specified <see cref="PipelineExecutionContext"/> and <see cref="StepExecutionContext"/>.
    /// </summary>
    public static IDictionary<object, object> CreateTriggerIndexingPropertiesFrom(Pipeline pipeline, IDictionary<string, object> input) =>
        new Dictionary<object, object>
        {
            [PipelineKey] = pipeline,
            [InputKey] = input
        };

    /// <param name="context">The context to start searching from.</param>
    extension(ExpressionExecutionContext context)
    {
        /// <summary>
        /// Returns the <see cref="Pipeline"/> of the specified <see cref="ExpressionExecutionContext"/>
        /// </summary>
        public bool TryGetPipelineExecutionContext(out PipelineExecutionContext pipelineExecutionContext) => context.TransientProperties.TryGetValue(PipelineExecutionContextKey, out pipelineExecutionContext!);

        /// <summary>
        /// Returns the <see cref="PipelineExecutionContext"/> of the specified <see cref="ExpressionExecutionContext"/>
        /// </summary>
        public PipelineExecutionContext GetPipelineExecutionContext()
        {
            return context.TransientProperties.TryGetValue(PipelineExecutionContextKey, out var value)
                ? (PipelineExecutionContext)value
                : throw new InvalidOperationException("PipelineExecutionContext not found. This value exists only on step execution contexts.");
        }

        /// <summary>
        /// Returns the <see cref="StepExecutionContext"/> of the specified <see cref="ExpressionExecutionContext"/>
        /// </summary>
        public StepExecutionContext GetStepExecutionContext()
        {
            return context.TransientProperties.TryGetValue(StepExecutionContextKey, out var value)
                ? (StepExecutionContext)value
                : throw new InvalidOperationException("StepExecutionContext not found. This value exists only on step execution contexts.");
        }

        /// <summary>
        /// Returns the <see cref="StepExecutionContext"/> of the specified <see cref="ExpressionExecutionContext"/>
        /// </summary>
        public bool TryGetStepExecutionContext(out StepExecutionContext stepExecutionContext) => context.TransientProperties.TryGetValue(StepExecutionContextKey, out stepExecutionContext!);

        /// <summary>
        /// Returns the <see cref="Step"/> of the specified <see cref="ExpressionExecutionContext"/>
        /// </summary>
        public IStep GetStep() => (IStep)context.TransientProperties[StepKey];

        /// <summary>
        /// Returns the value of the specified input.
        /// </summary>
        public T? Get<T>(Input<T>? input) => input != null ? context.GetBlock(input.MemoryBlockReference).Value.ConvertTo<T>() : default;

        /// <summary>
        /// Returns the value of the specified output.
        /// </summary>
        public T? Get<T>(Output output) => context.GetBlock(output.MemoryBlockReference).Value.ConvertTo<T>();

        /// <summary>
        /// Returns the value of the specified output.
        /// </summary>
        public object? Get(Output output) => context.GetBlock(output.MemoryBlockReference).Value;

        /// <summary>
        /// Returns the value of the variable with the specified name.
        /// </summary>
        public T? GetVariable<T>(string name)
        {
            var block = context.GetVariableBlock(name);
            return (T?)block?.Value;
        }

        /// <summary>
        /// Returns the variable with the specified name.
        /// </summary>
        public Variable? GetVariable(string name, bool localScopeOnly = false)
        {
            var block = context.GetVariableBlock(name, localScopeOnly);
            return block?.Metadata is VariableBlockMetadata metadata ? metadata.Variable : null;
        }

        private MemoryBlock? GetVariableBlock(string name, bool localScopeOnly = false)
        {
            foreach (var block in context.Memory.Blocks.Where(b => b.Value.Metadata is VariableBlockMetadata))
            {
                var metadata = block.Value.Metadata as VariableBlockMetadata;
                if (metadata!.Variable.Name == name)
                    return block.Value;
            }

            return localScopeOnly ? null : context.ParentContext?.GetVariableBlock(name);
        }

        /// <summary>
        /// Creates a named variable in the context.
        /// </summary>
        public Variable CreateVariable<T>(string name, T? value, Type? storageDriverType = null, Action<MemoryBlock>? configure = null)
        {
            var existingVariable = context.GetVariable(name, localScopeOnly: true);

            if (existingVariable != null)
                throw new($"Variable {name} already exists in the context.");

            var variable = new Variable(name, value)
            {
                StorageDriverType = storageDriverType ?? typeof(PipelineInstanceStorageDriver)
            };
        
            var parsedValue = variable.ParseValue(value);

            // Find the first parent context that has a variable container.
            // If not found, use the current context.
            var variableContainerContext = context.GetVariableContainerContext();

            variableContainerContext.Set(variable, parsedValue, configure);
            return variable;
        }

        /// <summary>
        /// Returns the first parent context that contains a variable container.
        /// </summary>
        public ExpressionExecutionContext GetVariableContainerContext()
        {
            return context.FindParent(x =>
            {
                var stepExecutionContext = x.TryGetStepExecutionContext(out var stepExecutionContextResult) ? stepExecutionContextResult : null;
                return stepExecutionContext?.Step is IVariableContainer;
            }) ?? context;
        }

        /// <summary>
        /// Sets the value of a named variable in the context.
        /// </summary>
        public Variable SetVariable<T>(string name, T? value, Action<MemoryBlock>? configure = null)
        {
            var variable = context.GetVariable(name);

            if (variable == null)
                return context.CreateVariable(name, value, configure: configure);

            // Get the context where the variable is defined.
            var contextWithVariable = context.FindContextContainingBlock(variable.Id) ?? context;

            // Set the value on the variable.
            var parsedValue = variable.ParseValue(value);
            variable.Set(contextWithVariable, parsedValue, configure);

            // Return the variable.
            return variable;
        }

        /// <summary>
        /// Sets the output to the specified value.
        /// </summary>
        public void Set(Output? output, object? value, Action<MemoryBlock>? configure = null)
        {
            if (output != null)
            {
                // Set the value on the output.
                var outputMemoryBlockReference = output.MemoryBlockReference();
                var parsedValue = output.ParseValue(value);
                context.Set(outputMemoryBlockReference, parsedValue, configure);

                // If the referenced output is a pipeline output definition, set the value on the pipeline execution context.
                var pipelineExecutionContext = context.GetPipelineExecutionContext();
                var pipeline = pipelineExecutionContext.Pipeline;
                var pipelineOutputDefinition = pipeline.Outputs.FirstOrDefault(x => x.Name == outputMemoryBlockReference.Id);

                if (pipelineOutputDefinition != null)
                    pipelineExecutionContext.Output[pipelineOutputDefinition.Name] = value!;
            }
        }

        /// <summary>
        /// Returns a dictionary of memory block keys and values across scopes.
        /// </summary>
        public IDictionary<string, object> ReadAndFlattenMemoryBlocks() =>
            context.FlattenMemoryBlocks().ToDictionary(x => x.Key, x => x.Value.Value!);

        /// <summary>
        /// Returns a dictionary of memory blocks across scopes.
        /// </summary>
        public IDictionary<string, MemoryBlock> FlattenMemoryBlocks()
        {
            var currentContext = context;
            var memoryBlocks = new Dictionary<string, MemoryBlock>();

            while (currentContext != null)
            {
                var register = currentContext.Memory;

                foreach (var entry in register.Blocks)
                    memoryBlocks.TryAdd(entry.Key, entry.Value);

                currentContext = currentContext.ParentContext;
            }

            return memoryBlocks;
        }

        /// <summary>
        /// Returns a the first context that contains a memory block with the specified ID.
        /// </summary>
        public ExpressionExecutionContext? FindContextContainingBlock(string blockId)
        {
            return context.FindParent(x => x.Memory.HasBlock(blockId));
        }

        /// <summary>
        /// Returns the first context in the hierarchy that matches the specified predicate.
        /// </summary>
        /// <param name="predicate">The predicate to match.</param>
        /// <returns>The first context that matches the predicate or <c>null</c> if no match was found.</returns>
        public ExpressionExecutionContext? FindParent(Func<ExpressionExecutionContext, bool> predicate)
        {
            var currentContext = context;

            while (currentContext != null)
            {
                if (predicate(currentContext))
                    return currentContext;

                currentContext = currentContext.ParentContext;
            }

            return null;
        }

        /// <summary>
        /// Returns the value of the specified variable.
        /// </summary>
        public object GetVariableInScope(string variableName)
        {
            var variable = context.GetVariable(variableName);
            var value = variable?.Get(context);

            return ConvertIEnumerableToArray(value);
        }

        /// <summary>
        /// Gets all variables names in scope.
        /// </summary>
        public IEnumerable<string> GetVariableNamesInScope() =>
            context.EnumerateVariablesInScope()
                .Select(x => x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct();

        /// <summary>
        /// Gets all variables in scope.
        /// </summary>
        public IEnumerable<Variable> GetVariablesInScope() =>
            context.EnumerateVariablesInScope()
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .DistinctBy(x => x.Name);

        /// <summary>
        /// Sets the value of a named variable in the context.
        /// </summary>
        public void SetVariableInScope(string variableName, object? value)
        {
            var q = from v in context.EnumerateVariablesInScope()
                where v.Name == variableName
                where v.TryGet(context, out _)
                select v;

            var variable = q.FirstOrDefault();

            if (variable != null)
                variable.Set(context, value);

            if (variable == null)
                context.CreateVariable(variableName, value);
        }

        /// <summary>
        /// Enumerates all variables in scope.
        /// </summary>
        public IEnumerable<Variable> EnumerateVariablesInScope()
        {
            var currentScope = context;

            while (currentScope != null)
            {
                if (!currentScope.TryGetStepExecutionContext(out var stepExecutionContext))
                {
                    var variables = currentScope.Memory.Blocks.Values
                        .Where(x => x.Metadata is VariableBlockMetadata)
                        .Select(x => x.Metadata as VariableBlockMetadata)
                        .Select(x => x!.Variable)
                        .ToList();

                    foreach (var variable in variables)
                        yield return variable;
                }
                else
                {
                    var variables = stepExecutionContext.Variables;

                    foreach (var variable in variables)
                        yield return variable;
                }

                currentScope = currentScope.ParentContext;
            }

            if (context.TryGetPipelineExecutionContext(out var pipelineExecutionContext))
            {
                if (pipelineExecutionContext.Pipeline.ResultVariable != null)
                    yield return pipelineExecutionContext.Pipeline.ResultVariable;
            }
        }

        /// <summary>
        /// Returns the input value associated with the specified <see cref="InputDefinition"/> in the given <see cref="ExpressionExecutionContext"/>.
        /// </summary>
        /// <typeparam name="T">The type of the input value.</typeparam>
        /// <param name="inputDefinition">The <see cref="InputDefinition"/> specifying the input to retrieve.</param>
        /// <returns>The input value associated with the specified <see cref="InputDefinition"/> in the <see cref="ExpressionExecutionContext"/>.</returns>
        public T? GetInput<T>(InputDefinition inputDefinition)
        {
            return context.GetInput<T>(inputDefinition.Name);
        }
    }

    private static JsonSerializerOptions? _serializerOptions;

    private static JsonSerializerOptions GetSerializerOptions(ExpressionExecutionContext context)
    {
        if (_serializerOptions != null)
            return _serializerOptions;

        var serializerOptions = context.GetRequiredService<IJsonSerializer>().GetOptions().Clone();
        serializerOptions.ReferenceHandler = ReferenceHandler.Preserve;
        _serializerOptions = serializerOptions;
        return serializerOptions;
    }

    extension(ExpressionExecutionContext context)
    {
        /// <summary>
        /// Returns the value of the specified input.
        /// </summary>
        /// <param name="name">The name of the input.</param>
        /// <typeparam name="T">The type of the input.</typeparam>
        /// <returns>The value of the specified input.</returns>
        public T? GetInput<T>(string name)
        {
            var value = context.GetInput(name);
            var serializerOptions = GetSerializerOptions(context);
            var converterOptions = new ObjectConverterOptions(serializerOptions);
            return value.ConvertTo<T>(converterOptions);
        }

        /// <summary>
        /// Returns the value of the specified input.
        /// </summary>
        /// <param name="name">The name of the input.</param>
        /// <returns>The value of the specified input.</returns>
        public object? GetInput(string name)
        {
            if (context.IsContainedWithinCompositeStep())
            {
                // If there's a variable in the current scope with the specified name, return that.
                var variable = context.GetVariable(name);

                if (variable != null)
                    return variable.Get(context);
            }

            // Otherwise, return the input.
            var pipelineExecutionContext = context.GetPipelineExecutionContext();
            var input = pipelineExecutionContext.Input;
            return input.TryGetValue(name, out var value) ? value : null;
        }

        /// <summary>
        /// Returns the value of the specified output.
        /// </summary>
        /// <param name="stepIdOrName">The ID or name of the step.</param>
        /// <param name="outputName">The name of the output.</param>
        /// <returns>The value of the specified output.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the step is not found.</exception>
        public object? GetOutput(string stepIdOrName, string? outputName)
        {
            var pipelineExecutionContext = context.GetPipelineExecutionContext();
            var stepExecutionContext = context.GetStepExecutionContext();
            var step = stepExecutionContext.FindStepByIdOrName(stepIdOrName);

            if (step == null)
                throw new InvalidOperationException("Step not found.");

            var outputRegister = pipelineExecutionContext.GetStepOutputRegister();
            var outputRecordCandidates = outputRegister.FindMany(step.Id, outputName);
            var containerIds = stepExecutionContext.GetAncestors().Select(x => x.Id).ToList();
            var filteredOutputRecordCandidates = outputRecordCandidates.Where(x => containerIds.Contains(x.ContainerId));
            var outputRecord = filteredOutputRecordCandidates.FirstOrDefault();
            return outputRecord?.Value;
        }

        /// <summary>
        /// Returns all step outputs.
        /// </summary>
        public async IAsyncEnumerable<StepOutputs> GetStepOutputs()
        {
            if (!context.TryGetStepExecutionContext(out var stepExecutionContext))
                yield break;

            var useStepName = stepExecutionContext.PipelineExecutionContext.Pipeline.CreatedWithModernTooling();
            var stepsWithOutputs = stepExecutionContext.GetStepsWithOutputs();

            if (useStepName)
                stepsWithOutputs = stepsWithOutputs.Where(x => !string.IsNullOrWhiteSpace(x.Step.Name));

            await foreach (var stepWithOutput in stepsWithOutputs.ConfigureAwait(false))
            {
                var step = stepWithOutput.Step;
                var stepDescriptor = stepWithOutput.StepDescriptor;
                var stepIdentifier = useStepName ? step.Name : step.Id;
                var stepIdPascalName = stepIdentifier.Pascalize();

                foreach (var output in stepDescriptor.Outputs)
                {
                    var outputPascalName = output.Name.Pascalize();
                    yield return new(step.Id, stepIdPascalName, [
                        outputPascalName
                    ]);
                }
            }
        }

        /// <summary>
        /// Returns a value indicating whether the current step is inside a composite step.
        /// </summary>
        public bool IsContainedWithinCompositeStep()
        {
            if (!context.TryGetStepExecutionContext(out var stepExecutionContext))
                return false;

            // If the first pipeline definition in the ancestor hierarchy and that pipeline definition has a parent, then we are inside a composite step.
            var firstPipelineContext = stepExecutionContext.GetAncestors().FirstOrDefault(x => x.Step is Pipeline);

            return firstPipelineContext?.ParentStepExecutionContext != null;
        }

        /// <summary>
        /// Returns the result of the step that was executed before the current step.
        /// </summary>
        public object? GetLastResult()
        {
            var pipelineExecutionContext = context.GetPipelineExecutionContext();
            return pipelineExecutionContext.GetLastStepResult();
        }

        /// <summary>
        /// Returns all step inputs.
        /// </summary>
        public IEnumerable<PipelineInput> GetPipelineInputs()
        {
            // Check if we are evaluating an expression during pipeline execution.
            if (context.TryGetPipelineExecutionContext(out var pipelineExecutionContext))
            {
                var input = pipelineExecutionContext.Input;

                foreach (var inputEntry in input)
                {
                    var inputPascalName = inputEntry.Key.Pascalize();
                    var inputValue = inputEntry.Value;
                    yield return new(inputPascalName, inputValue);
                }
            }
            else
            {
                // We end up here when we are evaluating an expression during trigger indexing.
                // The scenario being that a pipeline definition might have variables declared, that we want to be able to access from JavaScript expressions.
                foreach (var block in context.Memory.Blocks.Values)
                {
                    if (block.Metadata is not VariableBlockMetadata variableBlockMetadata)
                        continue;

                    var variable = variableBlockMetadata.Variable;
                    var variablePascalName = variable.Name.Pascalize();
                    yield return new(variablePascalName, block.Value);
                }
            }
        }
    }

    private static object ConvertIEnumerableToArray(object? obj)
    {
        if (obj == null)
            return null!;

        // If it's not an IEnumerable or it's a string or dictionary, return the original object.
        if (obj is not IEnumerable enumerable || obj is string || obj is IDictionary)
            return obj;

        // If this is an async enumerable, return as-is.
        if (obj.GetType().Name == "AsyncIListEnumerableAdapter`1")
            return obj;

        // Use LINQ to convert the IEnumerable to an array.
        // For projection operators like Select, the element type is the LAST generic argument
        // (e.g., ListSelectIterator<TSource, TResult> where TResult is the element type)
        var elementType = obj.GetType().GetGenericArguments().LastOrDefault();

        if (elementType == null)
            return obj;

        var toArrayMethod = typeof(Enumerable).GetMethod("ToArray")!.MakeGenericMethod(elementType);
        return toArrayMethod.Invoke(null, [
            enumerable
        ])!;
    }
}