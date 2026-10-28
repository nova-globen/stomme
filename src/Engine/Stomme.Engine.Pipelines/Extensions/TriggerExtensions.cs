using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Extension methods for <see cref="Trigger"/>.
/// </summary>
public static class TriggerExtensions
{
    /// <summary>
    /// Returns a filtered list of triggers that match the specified step type.
    /// </summary>
    /// <param name="triggers">The triggers to filter.</param>
    /// <typeparam name="T">The type of the step.</typeparam>
    public static IEnumerable<Trigger> Filter<T>(this IEnumerable<Trigger> triggers) where T : IStep
    {
        var stepTypeName = TypeNameHelper.GenerateTypeName<T>();
        return triggers.Where(x => x.Type == stepTypeName);
    }

    /// <summary>
    /// Creates an expression execution context for the specified trigger.
    /// </summary>
    /// <param name="trigger">The trigger for which to create an expression execution context.</param>
    /// <param name="stepDescriptor">The step descriptor.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="context">The pipeline indexing context.</param>
    /// <param name="expressionEvaluator">The expression evaluator.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>An expression execution context.</returns>
    public static async Task<ExpressionExecutionContext> CreateExpressionExecutionContextAsync(
        this ITrigger trigger,
        StepDescriptor stepDescriptor,
        IServiceProvider serviceProvider,
        PipelineIndexingContext context,
        IExpressionEvaluator expressionEvaluator,
        ILogger logger)
    {
        var namedInputs = trigger.GetNamedInputs();
        var assignedInputs = namedInputs.Where(x => x.Value.MemoryBlockReference != null!).ToList();
        var register = context.GetOrCreateRegister(trigger);
        var cancellationToken = context.CancellationToken;
        var expressionInput = new Dictionary<string, object>();
        var applicationProperties = ExpressionExecutionContextExtensions.CreateTriggerIndexingPropertiesFrom(context.Pipeline, expressionInput);
        applicationProperties[ExpressionExecutionContextExtensions.StepKey] = trigger;
        var expressionExecutionContext = new ExpressionExecutionContext(serviceProvider, register, null, applicationProperties, null, cancellationToken);

        // Evaluate step inputs before requesting trigger data.
        foreach (var namedInput in assignedInputs)
        {
            var inputDescriptor = stepDescriptor.Inputs.FirstOrDefault(x => x.Name == namedInput.Key);

            if (inputDescriptor == null)
            {
                logger.LogWarning("Input descriptor not found for input '{InputName}'", namedInput.Key);
                continue;
            }

            if (!inputDescriptor.AutoEvaluate)
            {
                logger.LogDebug("Skipping input '{InputName}' because it is not set to auto-evaluate.", namedInput.Key);
                continue;
            }

            var input = namedInput.Value;
            var locationReference = input.MemoryBlockReference();

            if (locationReference.Id == null!)
                continue;

            try
            {
                var value = await expressionEvaluator.EvaluateAsync(input, expressionExecutionContext).ConfigureAwait(false);
                locationReference.Set(expressionExecutionContext, value);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to evaluate '{@Expression}'", input.Expression);
            }
        }

        return expressionExecutionContext;
    }
}