using System.Diagnostics;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Expressions;

/// <summary>
/// Handles Input expressions.
/// </summary>
public class InputExpressionHandler : IExpressionHandler
{
    /// <inheritdoc />
    public ValueTask<object?> EvaluateAsync(Expression expression, Type returnType, ExpressionExecutionContext context, ExpressionEvaluatorOptions options)
    {
        object? result = null;
        var inputDefinition = expression.Value as InputDefinition;

        if (inputDefinition != null)
        {
            result = context.GetInput(inputDefinition.Name);
        }

        return ValueTask.FromResult(result);
    }
}