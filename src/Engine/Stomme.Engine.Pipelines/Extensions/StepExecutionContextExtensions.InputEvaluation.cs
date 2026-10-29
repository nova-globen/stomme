using System.Linq.Expressions;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

public static partial class StepExecutionContextExtensions
{
    /// <param name="context">The <see cref="StepExecutionContext"/> being extended.</param>
    extension(StepExecutionContext context)
    {
        /// <summary>
        /// Evaluates each input property of the step.
        /// </summary>
        public async Task EvaluateInputPropertiesAsync()
        {
            var stepDescriptor = context.StepDescriptor;
            var inputDescriptors = stepDescriptor.Inputs.Where(x => x.AutoEvaluate).ToList();

            // Evaluate inputs.
            foreach (var inputDescriptor in inputDescriptors)
                await EvaluateInputPropertyAsync(context, stepDescriptor, inputDescriptor).ConfigureAwait(false);

            context.SetHasEvaluatedProperties();
        }

        /// <summary>
        /// Evaluates the specified input property of the step.
        /// </summary>
        public async Task<T?> EvaluateInputPropertyAsync<TStep, T>(Expression<Func<TStep, Input<T>>> propertyExpression)
        {
            var inputName = propertyExpression.GetProperty()!.Name;
            var input = await EvaluateInputPropertyAsync(context, inputName).ConfigureAwait(false);
            return input.ConvertTo<T>();
        }

        /// <summary>
        /// Evaluates a specific input property of the step.
        /// </summary>
        public async Task<object?> EvaluateInputPropertyAsync(string inputName)
        {
            var step = context.Step;
            var stepRegistryLookup = context.GetRequiredService<IStepRegistryLookupService>();
            var stepDescriptor = await stepRegistryLookup.FindAsync(step.Type).ConfigureAwait(false) ?? throw new Exception("Step descriptor not found");
            var inputDescriptor = stepDescriptor.GetWrappedInputPropertyDescriptor(step, inputName);

            if (inputDescriptor == null)
                throw new Exception($"No input with name {inputName} could be found");

            return await EvaluateInputPropertyAsync(context, stepDescriptor, inputDescriptor).ConfigureAwait(false);
        }

        /// <summary>
        /// Evaluates the specified input and sets the result in the step execution context's memory space.
        /// </summary>
        /// <param name="input">The input to evaluate.</param>
        /// <typeparam name="T">The type of the input.</typeparam>
        /// <returns>The evaluated value.</returns>
        public async Task<T?> EvaluateAsync<T>(Input<T> input)
        {
            var evaluator = context.GetRequiredService<IExpressionEvaluator>();
            var memoryBlockReference = input.MemoryBlockReference();
            var value = await evaluator.EvaluateAsync(input, context.ExpressionExecutionContext).ConfigureAwait(false);
            memoryBlockReference.Set(context, value);
            return value;
        }

        private async Task<object?> EvaluateInputPropertyAsync(StepDescriptor stepDescriptor, InputDescriptor inputDescriptor)
        {
            try
            {
                return await EvaluateInputPropertyCoreAsync(context, stepDescriptor, inputDescriptor).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The cause belongs in the message: a run's incident shows this message alone, and "failed to evaluate" without
                // the reason (an expression naming no variable, say) left the author guessing.
                throw new InputEvaluationException(inputDescriptor.Name, $"Failed to evaluate step input '{inputDescriptor.Name}': {e.Message}", e);
            }
        }

        private async Task<object?> EvaluateInputPropertyCoreAsync(StepDescriptor stepDescriptor, InputDescriptor inputDescriptor)
        {
            var step = context.Step;
            var defaultValue = inputDescriptor.DefaultValue;
            var value = defaultValue;
            var input = inputDescriptor.ValueGetter(step);
            var identityGenerator = context.GetRequiredService<IIdentityGenerator>();

            if (inputDescriptor.IsWrapped)
            {
                var wrappedInput = (Input?)input;

                if (defaultValue != null && wrappedInput == null)
                {
                    var typedInput = typeof(Input<>).MakeGenericType(inputDescriptor.Type);
                    var valueExpression = new Literal(defaultValue)
                    {
                        Id = identityGenerator.GenerateId(),
                    };
                    wrappedInput = (Input)Activator.CreateInstance(typedInput, valueExpression)!;
                    inputDescriptor.ValueSetter(step, wrappedInput);
                }
                else
                {
                    var expressionEvaluator = context.GetRequiredService<IExpressionEvaluator>();
                    var expressionExecutionContext = context.ExpressionExecutionContext;
                    var inputEvaluatorType = inputDescriptor.EvaluatorType ?? typeof(DefaultStepInputEvaluator);

                    if (wrappedInput?.Expression != null)
                    {
                        var inputEvaluator = (IStepInputEvaluator)context.GetRequiredService(inputEvaluatorType);
                        var inputEvaluatorContext = new StepInputEvaluatorContext(context, expressionExecutionContext, inputDescriptor, wrappedInput, expressionEvaluator);
                        value = await inputEvaluator.EvaluateAsync(inputEvaluatorContext).ConfigureAwait(false);
                    }
                }

                var memoryReference = wrappedInput?.MemoryBlockReference();

                if (memoryReference != null)
                {
                    // When input is created from a step provider, there may be no memory block reference ID.
                    if (memoryReference.Id == null!) 
                        memoryReference.Id = $"{step.NodeId}.{inputDescriptor.Name}"; // Construct a deterministic ID.
                
                    // Declare the input memory block in the current context. 
                    context.ExpressionExecutionContext.Set(memoryReference, value!);
                }
            }
            else
            {
                value = input;
            }

            await StoreInputValueAsync(context, inputDescriptor, value!).ConfigureAwait(false);

            return value;
        }
    }

    private static Task StoreInputValueAsync(StepExecutionContext context, InputDescriptor inputDescriptor, object value)
    {
        // Store the serialized input value in the step state.
        // Serializing the value ensures we store a copy of the value and not a reference to the input, which may change over time.
        if (inputDescriptor.IsSerializable != false)
        {
            // TODO: Disable filtering for now until we redesign log sanitization.
            // var serializedValue = await context.GetRequiredService<ISafeSerializer>().SerializeToElementAsync(value);
            // var manager = context.GetRequiredService<IStepStateFilterManager>();
            // var filterContext = new StepStateFilterContext(context, inputDescriptor, serializedValue, context.CancellationToken);
            // var filterResult = await manager.RunFiltersAsync(filterContext);
            context.StepState[inputDescriptor.Name] = value;
        }

        return Task.CompletedTask;
    }
}