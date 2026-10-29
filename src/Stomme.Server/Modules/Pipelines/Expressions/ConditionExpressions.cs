#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Expressions;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Models.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Pipelines.Expressions
{
    /// <summary>
    /// The engine's <c>Condition</c> expression kind: a <see cref="PipelineCondition"/> - rules comparing
    /// two templated sides, all or any of which must hold - evaluated when the step runs from the pipeline's variables in scope by
    /// <see cref="PipelineConditions"/>, the same code the editor previews with. What a Decision, an If and a While test and a
    /// Switch (flow) case matches on. No code runs; a name no variable has faults the step, saying which.
    /// </summary>
    public sealed class ConditionExpressionHandler : IExpressionHandler
    {
        public const string Kind = "Condition";

        public async ValueTask<object?> EvaluateAsync(Expression expression, Type returnType, ExpressionExecutionContext context, ExpressionEvaluatorOptions options)
        {
            var text = expression.Value switch
            {
                string value => value,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                JsonElement element => element.GetRawText(),
                null => null,
                var other => other.ToString(),
            };
            return PipelineConditions.Evaluate(PipelineConditions.Read(text), await BuiltInValues.LookupAsync(context).ConfigureAwait(false));
        }
    }

    /// <summary>Registers <see cref="ConditionExpressionHandler"/> with the engine, so a definition holding one reads and runs.</summary>
    public sealed class ConditionExpressionDescriptorProvider : IExpressionDescriptorProvider
    {
        public IEnumerable<ExpressionDescriptor> GetDescriptors()
        {
            yield return new ExpressionDescriptor
            {
                Type = ConditionExpressionHandler.Kind,
                DisplayName = "Condition",
                IsSerializable = true,
                IsBrowsable = true,
                HandlerFactory = services => ActivatorUtilities.GetServiceOrCreateInstance<ConditionExpressionHandler>(services),
                MemoryBlockReferenceFactory = () => new MemoryBlockReference(),
            };
        }
    }
}
