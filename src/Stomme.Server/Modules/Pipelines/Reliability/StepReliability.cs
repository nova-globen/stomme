#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Reliability
{
    /// <summary>
    /// A step's reliability settings, as the graph codec keeps them on the step's custom properties: how
    /// many times a failed step is tried again and how long to wait between tries, and whether the pipeline goes on past it
    /// when it still fails. Bounded here, wherever they come from.
    /// </summary>
    public static class StepReliability
    {
        public const string RetriesKey = "stomme:retries";

        public const string RetryDelayKey = "stomme:retryDelaySeconds";

        public const string ContinueOnErrorKey = "stomme:continueOnError";

        /// <summary>n8n's error output: a step that still fails goes out through its Failed outcome.</summary>
        public const string FailedPathKey = "stomme:failedPath";

        /// <summary>The variable (id) the failure is saved to when the pipeline goes on past it.</summary>
        public const string FailureVariableKey = "stomme:failureVariable";

        /// <summary>On a step's record when it failed and the pipeline went on past it: how (<see cref="PipelineStepRunView.HandledFailure"/>)...</summary>
        public const string HandledFailureKey = "stomme:handledFailure";

        /// <summary>...and the failure's message.</summary>
        public const string FailureMessageKey = "stomme:failure";

        /// <summary>The handled failure a step's record carries - how and the message - or nulls.</summary>
        public static (string? Handled, string? Message) HandledFailure(IDictionary<string, object>? metadata) =>
            metadata is null ? (null, null) : (Text(metadata, HandledFailureKey), Text(metadata, FailureMessageKey));

        /// <summary>Whether the step takes its Failed path, and the variable its failure is saved to (null for none).</summary>
        public static (bool FailedPath, string? FailureVariable) OnFailure(IDictionary<string, object>? properties) =>
            properties is null ? (false, null) : (Flag(properties, FailedPathKey), Text(properties, FailureVariableKey));

        /// <summary>A step switched off in the editor (n8n's "deactivate"): skipped when the run reaches it.</summary>
        public const string DisabledKey = "stomme:disabled";

        public static bool IsDisabled(IDictionary<string, object>? properties) => properties is not null && Flag(properties, DisabledKey);

        /// <summary>
        /// BL-229 (n8n's pinned data): the result a TEST hands on in place of running the step. Only the step test puts it on a
        /// step - on a body it runs and stores nowhere, never through the codec - so no published run holds one; and it is read
        /// only in a step test besides (<see cref="Application.StepTestCommitGate"/>).
        /// </summary>
        public const string PinnedKey = "stomme:pinned";

        public static string? Pinned(IDictionary<string, object>? properties) => properties is null ? null : Text(properties, PinnedKey);

        public const int MaxRetries = 5;

        public const int MaxRetryDelaySeconds = 60;

        public static (int Retries, int RetryDelaySeconds, bool ContinueOnError) Of(IDictionary<string, object>? properties)
        {
            if (properties is null)
            {
                return (0, 0, false);
            }

            return (
                Math.Clamp(Whole(properties, RetriesKey), 0, MaxRetries),
                Math.Clamp(Whole(properties, RetryDelayKey), 0, MaxRetryDelaySeconds),
                Flag(properties, ContinueOnErrorKey));
        }

        /// <summary>The engine's step pipeline gains the retry and continue-on-error, just inside its exception handling.</summary>
        public static void UseStepReliability(this Stomme.Engine.Features.Services.IModule module) =>
            module.Configure<EngineFeature>(engine => engine.ConfigureStepExecutionPipeline += builder =>
                builder.Insert(1, next => context => new StepReliabilityMiddleware(next).InvokeAsync(context)));

        // A value as the engine hands custom properties back: a JSON element after a round trip, a number or text before.
        private static int Whole(IDictionary<string, object> properties, string key) => properties.TryGetValue(key, out var value) ? value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            IConvertible convertible when int.TryParse(Convert.ToString(convertible, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var converted) => converted,
            _ => 0,
        } : 0;

        private static string? Text(IDictionary<string, object> properties, string key) => properties.TryGetValue(key, out var value) ? value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element when !string.IsNullOrWhiteSpace(element.GetString()) => element.GetString()!.Trim(),
            string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
            _ => null,
        } : null;

        private static bool Flag(IDictionary<string, object> properties, string key) => properties.TryGetValue(key, out var value) && value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } element => string.Equals(element.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// Runs a step again when it fails, as often and as far apart as it says, and - when it says so - lets the pipeline go on
    /// past it when it still fails: the step completes (its "done" outcome) with the failure in the run's log, instead of
    /// faulting the run. Every try and the giving up are logged. A step with no settings passes straight through.
    /// </summary>
    public sealed class StepReliabilityMiddleware(StepMiddlewareDelegate next) : IStepExecutionMiddleware
    {
        public async ValueTask InvokeAsync(StepExecutionContext context)
        {
            // A step switched off is skipped - nothing it does runs, a body it holds included - and the run goes on through its
            // first outcome (a data step's Done, a Decision's True), so what came before reaches what comes after unchanged. The
            // engine's invoker still runs around it (measured: completing the step here, without it, left the run Running); only
            // the step's own work is swapped, through the invoker's execute delegate, which it resets afterwards.
            if (StepReliability.IsDisabled(context.Step.CustomProperties))
            {
                context.PipelineExecutionContext.ExecuteDelegate = SkipAsync;
                await next(context);
                return;
            }

            // BL-229 (n8n's pinned data): in a test, a step its author pinned hands its pinned result on in place of running - and
            // goes on through its first outcome, as one switched off does.
            if (StepReliability.Pinned(context.Step.CustomProperties) is { } pinned && Application.StepTestCommitGate.IsStepTest(context.PipelineExecutionContext))
            {
                context.PipelineExecutionContext.ExecuteDelegate = step => UsePinnedAsync(step, pinned);
                await next(context);
                return;
            }

            var (retries, delaySeconds, continueOnError) = StepReliability.Of(context.Step.CustomProperties);
            var (failedPath, failureVariable) = StepReliability.OnFailure(context.Step.CustomProperties);
            if (retries == 0 && !continueOnError && !failedPath)
            {
                await next(context);
                return;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await next(context);
                    return;
                }
                catch (Exception ex) when (attempt <= retries && !context.CancellationToken.IsCancellationRequested)
                {
                    context.AddExecutionLogEntry("Retrying", $"Try {attempt} failed: {ex.Message} Trying again{(delaySeconds > 0 ? $" in {delaySeconds} s" : "")} ({attempt} of {retries}).");
                    if (delaySeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), context.CancellationToken);
                    }
                }
                catch (Exception ex) when ((failedPath || continueOnError) && !context.CancellationToken.IsCancellationRequested)
                {
                    // The run's execute delegate - a resumed step's callback (Execute pipeline's, when the pipeline it waited for
                    // failed) - is let go of by the engine's invoker only when the step's work returns; thrown, it stays, and
                    // the step the run goes on to would run that callback as its own work (measured: the Failed path's step
                    // failed again with the call's failure). The step is done with it either way.
                    context.PipelineExecutionContext.ExecuteDelegate = null;
                    var saved = SaveFailure(context, failureVariable, ex);

                    // On the step's record (its metadata is kept whole), so a run's view says the step failed though it completed.
                    context.Metadata[StepReliability.HandledFailureKey] = failedPath ? PipelineStepRunView.TookFailedPath : PipelineStepRunView.WentOn;
                    context.Metadata[StepReliability.FailureMessageKey] = ex.Message;
                    if (failedPath)
                    {
                        // n8n's error output: the failure is a branch of the flow - whatever is connected to Failed runs next.
                        context.AddExecutionLogEntry("TookFailedPath", $"Failed: {ex.Message} The pipeline goes on through its Failed path.{saved}");
                        await context.CompleteStepWithOutcomesAsync(PipelineOutcomes.Failed);
                    }
                    else
                    {
                        context.AddExecutionLogEntry("ContinuedAfterFailure", $"Failed: {ex.Message} The pipeline goes on (continue on error).{saved}");
                        await context.CompleteStepAsync();
                    }

                    return;
                }
            }
        }

        // The failure, as JSON text in the variable the step names - {"error": ..., "step": ...}, read with {{ failure.error }}.
        // A variable the step no longer has in scope is said in the log, never a second failure.
        private static string SaveFailure(StepExecutionContext context, string? variableId, Exception ex)
        {
            if (variableId is null)
            {
                return "";
            }

            var variable = context.ExpressionExecutionContext.EnumerateVariablesInScope().FirstOrDefault(candidate => candidate.Id == variableId);
            if (variable is null)
            {
                return " Its error could not be saved: the variable it names is not in the pipeline.";
            }

            var stepName = string.IsNullOrWhiteSpace(context.Step.Name) ? context.Step.Type : context.Step.Name;
            variable.Set(context, JsonSerializer.Serialize(new Dictionary<string, string?> { ["error"] = ex.Message, ["step"] = stepName }));
            return $" Its error is saved to '{variable.Name}'.";
        }

        private static async ValueTask UsePinnedAsync(StepExecutionContext context, string pinned)
        {
            context.AddExecutionLogEntry("Pinned", "The step's pinned result is handed on in place of running it - in a test; a run runs it.");
            // Set as a step's own result is: under the name "Result" - the output register keys outputs by name and the next step's
            // "previous result" is the one named so - and whether or not a variable is bound to it (an unbound Result is null, and
            // the register still records it).
            if (context.Step is IStepWithResult { Result: var output })
            {
                context.Set(output, pinned, Stomme.Engine.Pipelines.Models.StepOutputRegister.DefaultOutputName);
            }

            var first = context.StepDescriptor.Ports.FirstOrDefault(port => port.Type == Stomme.Engine.Pipelines.Models.PortType.Flow)?.Name;
            if (first is null)
            {
                await context.CompleteStepAsync();
            }
            else
            {
                await context.CompleteStepWithOutcomesAsync(first);
            }
        }

        private static async ValueTask SkipAsync(StepExecutionContext context)
        {
            context.AddExecutionLogEntry("Skipped", "The step is switched off in the editor; the pipeline goes on past it.");
            var first = context.StepDescriptor.Ports.FirstOrDefault(port => port.Type == Stomme.Engine.Pipelines.Models.PortType.Flow)?.Name;
            if (first is null)
            {
                await context.CompleteStepAsync();
            }
            else
            {
                await context.CompleteStepWithOutcomesAsync(first);
            }
        }
    }
}
