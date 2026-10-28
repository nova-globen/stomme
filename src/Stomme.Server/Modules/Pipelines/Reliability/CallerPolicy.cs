#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;

namespace Stomme.Modules.Pipelines.Reliability
{
    /// <summary>
    /// BL-229 - n8n's "This workflow can be called by", held where a pipeline calls another (<see cref="PipelineCallers"/>): the
    /// CALLED pipeline's published version says which pipelines may call it, and a call it does not allow fails at the caller's
    /// step - before the called pipeline runs, stores or sends anything - saying why.
    /// </summary>
    public static class PipelineCallerPolicy
    {
        /// <summary>
        /// On a step test's run: the pipeline whose editor tests it. A step test runs a copy of the graph under an identity of its
        /// own, so a call it makes is the call that pipeline would make.
        /// </summary>
        public const string TestedPipelineKey = "stomme:testedPipeline";

        /// <summary>The engine's steps that start another pipeline, each naming it in its <c>PipelineDefinitionId</c>.</summary>
        public static readonly IReadOnlySet<string> CallTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Stomme.ExecutePipeline", "Stomme.DispatchPipeline", "Stomme.BulkDispatchPipelines",
        };

        /// <summary>The pipeline a run's calls come from: its own, or - on a step test - the pipeline tested.</summary>
        public static string? CallerOf(PipelineExecutionContext run) =>
            StepTestCommitGate.IsStepTest(run) && run.Properties.TryGetValue(TestedPipelineKey, out var tested) && tested is string { Length: > 0 } id
                ? id
                : run.Pipeline.Identity.DefinitionId;

        /// <summary>
        /// Why the pipeline called refuses the caller, or null when it admits it - or has no published version, which the call
        /// itself reports.
        /// </summary>
        public static async Task<string?> RefusalAsync(IPipelineDefinitionStore definitions, string calledDefinitionId, string? callerDefinitionId, CancellationToken cancellationToken)
        {
            var called = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = calledDefinitionId, VersionOptions = VersionOptions.Published }, cancellationToken).ConfigureAwait(false);
            if (called is null)
            {
                return null;
            }

            var setting = PipelineGraphCodec.CalledBy(called.StringData);
            return PipelineCallers.Admits(setting, callerDefinitionId)
                ? null
                : PipelineCallers.Refusal(setting, string.IsNullOrWhiteSpace(called.Name) ? calledDefinitionId : called.Name);
        }

        /// <summary>
        /// The engine's step pipeline gains the check, inside the retry - so a refused call takes the step's Failed path or goes
        /// on when the step says so, as any failure of the step does. Added BEFORE the retry: each one is put in at the same place,
        /// so the one added first ends up innermost.
        /// </summary>
        public static void UseCallerPolicy(Stomme.Engine.Features.Services.IModule module) =>
            module.Configure<EngineFeature>(engine => engine.ConfigureStepExecutionPipeline += builder =>
                builder.Insert(1, next => context => new CallerPolicyMiddleware(next).InvokeAsync(context)));
    }

    /// <summary>
    /// A call step's work begins by asking the pipeline named whether this run's pipeline may call it
    /// (<see cref="PipelineCallerPolicy"/>): the check is the step's work - put in as the run's execute delegate, as a step
    /// switched off is skipped - so a refusal fails a RUNNING step, which its Failed path or "go on" can take (a step not yet
    /// running cannot be completed: measured, the run was left suspended). Only as the call starts: a call the engine resumes
    /// when its pipeline has ended has its callback as the delegate then, and was let through when it started; a step switched
    /// off starts nothing; and a call the engine runs in the background is checked when it runs there.
    /// </summary>
    public sealed class CallerPolicyMiddleware(StepMiddlewareDelegate next) : IStepExecutionMiddleware
    {
        public async ValueTask InvokeAsync(StepExecutionContext context)
        {
            if (context.PipelineExecutionContext.ExecuteDelegate is null && PipelineCallerPolicy.CallTypes.Contains(context.Step.Type) && !GoesToTheBackground(context))
            {
                context.PipelineExecutionContext.ExecuteDelegate = CheckThenCallAsync;
            }

            await next(context).ConfigureAwait(false);
        }

        private static async ValueTask CheckThenCallAsync(StepExecutionContext context)
        {
            // The delegate is the run's, not the step's: let go of it first, so whatever this does, the next step runs its own work.
            context.PipelineExecutionContext.ExecuteDelegate = null;
            if (await context.EvaluateInputPropertyAsync(PipelineCallContracts.CalledInput).ConfigureAwait(false) is string { Length: > 0 } called
                && await PipelineCallerPolicy.RefusalAsync(
                    context.GetRequiredService<IPipelineDefinitionStore>(), called, PipelineCallerPolicy.CallerOf(context.PipelineExecutionContext), context.CancellationToken).ConfigureAwait(false) is { } refusal)
            {
                throw new FaultException("CALL_REFUSED", "Pipelines", "Security", refusal);
            }

            await context.Step.ExecuteAsync(context).ConfigureAwait(false);
        }

        // The engine's background invoker sends a job, or a task set to run asynchronously, to the background - unless a delegate
        // is set; setting one here would keep it in the foreground.
        private static bool GoesToTheBackground(StepExecutionContext context) =>
            !context.TransientProperties.ContainsKey(BackgroundStepExecutionContextExtensions.IsBackgroundExecution)
            && (context.StepDescriptor.Kind is StepKind.Job
                || context.StepDescriptor.Kind is StepKind.Task && (context.Step.GetRunAsynchronously() ?? context.StepDescriptor.RunAsynchronously));
    }
}
