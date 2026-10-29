#nullable enable
using System;
using System.Collections.Generic;
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
    /// The engine's <c>Template</c> expression kind: an input written as text with <c>{{ name.path }}</c>
    /// placeholders, rendered when the step runs from the built-ins (<see cref="PipelineBuiltIns"/>) and the pipeline's
    /// variables in scope - by <see cref="PipelineTemplate"/>, the
    /// same code the editor previews with. No code runs; a name no variable in scope has faults the step, saying which.
    /// </summary>
    public sealed class TemplateExpressionHandler : IExpressionHandler
    {
        public const string Kind = "Template";

        public async ValueTask<object?> EvaluateAsync(Expression expression, Type returnType, ExpressionExecutionContext context, ExpressionEvaluatorOptions options)
        {
            var template = expression.Value as string ?? expression.Value?.ToString();
            return PipelineTemplate.Render(template, await BuiltInValues.LookupAsync(context).ConfigureAwait(false));
        }
    }

    /// <summary>Registers <see cref="TemplateExpressionHandler"/> with the engine, so a definition holding one reads and runs.</summary>
    public sealed class TemplateExpressionDescriptorProvider : IExpressionDescriptorProvider
    {
        public IEnumerable<ExpressionDescriptor> GetDescriptors()
        {
            yield return new ExpressionDescriptor
            {
                Type = TemplateExpressionHandler.Kind,
                DisplayName = "Template",
                IsSerializable = true,
                IsBrowsable = true,
                HandlerFactory = services => ActivatorUtilities.GetServiceOrCreateInstance<TemplateExpressionHandler>(services),
                MemoryBlockReferenceFactory = () => new MemoryBlockReference(),
            };
        }
    }
}
