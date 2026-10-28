using System.Text.Json;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.UIHints.Dictionary;

/// <summary>
/// A class that evaluates step inputs configured to be dictionaries. It resolves expressions and modifies the input dictionary accordingly.
/// </summary>
public class DictionaryValueEvaluator(ILogger<DictionaryValueEvaluator> logger) : IStepInputEvaluator
{
    public async Task<object?> EvaluateAsync(StepInputEvaluatorContext context)
    {
        var wrappedInput = context.Input;
        var evaluator = context.ExpressionEvaluator;
        var expressionExecutionContext = context.ExpressionExecutionContext;
        var inputDescriptor = context.InputDescriptor;
        var defaultValue = inputDescriptor.DefaultValue;
        var value = wrappedInput.Expression != null ? await evaluator.EvaluateAsync(wrappedInput, expressionExecutionContext).ConfigureAwait(false) : defaultValue;
        
        if (value is not IDictionary<string, object> dictionary || inputDescriptor.UIHint != InputUIHints.Dictionary)
            return value;

        var tempDictionary = new Dictionary<string, object?>(dictionary.Count);
        foreach (var dict in dictionary)
        {
            if (dict.Value is not JsonElement json)
            {
                // Not a JSON object, so just use the value as-is.
                tempDictionary[dict.Key] = dict.Value;
                continue;
            }

            // JSON object, so extract the type and value properties.
            var hasType = json.TryGetProperty("type", out var typeProperty);
            var hasValue = json.TryGetProperty("value", out var valueProperty);

            if (!hasType || !hasValue)
            {
                // Skip this entry or handle as needed (e.g., log, throw, etc.)
                logger.LogWarning("Dictionary entry is missing type or value property: {Json}", JsonSerializer.Serialize(json));
                continue;
            }
                
            // Evaluate the expression.
            var expression = new Expression(typeProperty.ToString(), valueProperty.ToString());
            var val = await evaluator.EvaluateAsync<object>(expression, expressionExecutionContext).ConfigureAwait(false);

            // Add the evaluated value to the dictionary.
            tempDictionary[dict.Key] = val;
        }

        // Replace the original dictionary with the evaluated one.
        value = tempDictionary;

        return value;
    }
}