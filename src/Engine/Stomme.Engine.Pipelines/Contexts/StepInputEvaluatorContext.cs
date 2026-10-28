using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

public record StepInputEvaluatorContext(
    StepExecutionContext StepExecutionContext, 
    ExpressionExecutionContext ExpressionExecutionContext, 
    InputDescriptor InputDescriptor, 
    Input Input, 
    IExpressionEvaluator ExpressionEvaluator);