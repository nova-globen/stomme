#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Pipelines.Expressions
{
    /// <summary>
    /// The run's facts for <see cref="PipelineBuiltIns"/> (BL-229), read from the engine when an expression or a condition is
    /// evaluated: the moment from the engine's clock, the run and the pipeline from the run's context, the site's variables from
    /// the store once per scope (<see cref="PipelineSiteVariablesCache"/>).
    /// </summary>
    internal static class BuiltInValues
    {
        public static async Task<PipelineRunFacts> OfAsync(ExpressionExecutionContext context)
        {
            var now = context.ServiceProvider.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;
            IReadOnlyDictionary<string, string>? variables = null;
            if (context.ServiceProvider.GetService<PipelineSiteVariablesCache>() is { } cache)
            {
                variables = await cache.GetAsync(context.CancellationToken).ConfigureAwait(false);
            }

            if (!context.TryGetPipelineExecutionContext(out var run))
            {
                return new PipelineRunFacts(now, SiteVariables: variables);
            }

            var pipeline = run.Pipeline;
            return new PipelineRunFacts(now, run.Id, run.CorrelationId, run.CreatedAt, pipeline.Identity.DefinitionId, pipeline.PipelineMetadata.Name, pipeline.Identity.Version, variables,
                PipelineBuiltIns.InputOf(run.Input?.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value))),
                PipelineGraphCodec.TimeZone(pipeline.Root.Metadata)); // BL-229: the zone of the version the run runs
        }

        /// <summary>The built-ins, then the variables in scope by name.</summary>
        public static async Task<Func<string, (bool Found, object? Value)>> LookupAsync(ExpressionExecutionContext context) =>
            PipelineBuiltIns.With(name => context.GetVariable(name) is { } variable ? (true, variable.Get(context)) : (false, null), await OfAsync(context).ConfigureAwait(false));
    }
}
