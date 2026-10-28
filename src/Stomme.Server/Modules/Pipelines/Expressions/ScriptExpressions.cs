#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Expressions;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Expressions
{
    /// <summary>
    /// The engine's <c>JavaScript</c> expression kind (BL-229 increment 5b): an input computed by a JavaScript expression in the
    /// module's ONE bounded sandbox (<see cref="JavaScriptSandbox"/> - strict, no host access, bounded by statements, time and
    /// memory), with the pipeline's variables in scope by name - a record or a list as the JavaScript value it is. Only where
    /// <c>Pipelines:Scripts:Enabled</c>: elsewhere the step faults saying so, as the Run script step does.
    /// </summary>
    public sealed class ScriptExpressionHandler(IOptions<PipelineStepOptions> options) : IExpressionHandler
    {
        public const string Kind = "JavaScript";

        public ValueTask<object?> EvaluateAsync(Expression expression, Type returnType, ExpressionExecutionContext context, ExpressionEvaluatorOptions evaluatorOptions)
        {
            var scripts = options.Value.Scripts;
            if (!scripts.Enabled)
            {
                throw new InvalidOperationException("JavaScript expressions are switched off on this site (Pipelines:Scripts:Enabled).");
            }

            var script = expression.Value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                null => null,
                var other => other.ToString(),
            };
            var values = context.GetVariablesInScope().ToDictionary(variable => variable.Name, variable => variable.Get(context), StringComparer.Ordinal);
            var evaluation = JavaScriptSandbox.EvaluateValue(script ?? string.Empty, values, scripts);
            if (!evaluation.Succeeded)
            {
                throw new InvalidOperationException("The JavaScript expression failed or exceeded its sandbox limits: " + evaluation.Error);
            }

            return ValueTask.FromResult(evaluation.Value);
        }
    }

    /// <summary>Registers <see cref="ScriptExpressionHandler"/> with the engine, so a definition holding one reads and runs.</summary>
    public sealed class ScriptExpressionDescriptorProvider : IExpressionDescriptorProvider
    {
        public IEnumerable<ExpressionDescriptor> GetDescriptors()
        {
            yield return new ExpressionDescriptor
            {
                Type = ScriptExpressionHandler.Kind,
                DisplayName = "JavaScript",
                IsSerializable = true,
                IsBrowsable = true,
                HandlerFactory = services => ActivatorUtilities.GetServiceOrCreateInstance<ScriptExpressionHandler>(services),
                MemoryBlockReferenceFactory = () => new MemoryBlockReference(),
            };
        }
    }
}
