#nullable enable
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What a data step does when its work fails as a matter of course (a failed validation, a non-2xx response).</summary>
    public enum FailurePolicy
    {
        /// <summary>The run faults with the error (the predecessor platform's "Stop", the default).</summary>
        Fault,

        /// <summary>The step completes through its <c>Failed</c> port with the error in its <c>Error</c> output (the predecessor platform's "Branch").</summary>
        Continue,
    }

    /// <summary>
    /// The base of the step families absorbed from the predecessor platform (BL-95 increment (d), D111): a step whose RESULT is text -
    /// records as a JSON array, a document, a response body - and whose failure is either a fault or an outcome.
    /// <para>
    /// <b>The records convention.</b> the predecessor platform passed "the upstream step's payload" implicitly; the engine's own
    /// equivalent is the LAST RESULT, which it records whenever a step sets its <c>Result</c> output. A data step's
    /// text input therefore falls back to the last result when left empty, so a chain (parse, map, validate, write)
    /// authors without a variable, while an explicit value or a variable-bound input still wins.
    /// </para>
    /// <para>
    /// <b>Failure.</b> <see cref="FailAsync"/> honours <see cref="OnFailure"/>: <see cref="FailurePolicy.Fault"/>
    /// throws the engine's <see cref="FaultException"/> so the run faults with the code and message;
    /// <see cref="FailurePolicy.Continue"/> completes through the <c>Failed</c> port with the <see cref="Error"/>
    /// output set. Either way the failure is an execution-log entry on the step. An error in the step's
    /// CONFIGURATION (a missing required input) throws and faults regardless: that is the author's mistake, not data.
    /// </para>
    /// </summary>
    public abstract class DataStep : Step<string>
    {
        public const string DoneOutcome = "Done";
        public const string FailedOutcome = "Failed";

        protected DataStep(string? source = null, int? line = null) : base(source, line)
        {
        }

        [Input(DisplayName = "On failure", Description = "Fault: the run faults with the error. Continue: the step completes through its Failed port and the error is in its Error output.", Category = "Failure", Order = 100)]
        public Input<FailurePolicy> OnFailure { get; set; } = new(new Literal<FailurePolicy>(FailurePolicy.Fault));

        [Output(Description = "The error's code and message when the step failed; empty when it succeeded.")]
        public Output<string?>? Error { get; set; }

        protected override ValueTask ExecuteAsync(StepExecutionContext context) => RunAsync(context);

        protected abstract ValueTask RunAsync(StepExecutionContext context);

        /// <summary>The input's value, or the last result when the input is empty (the records convention).</summary>
        protected static string? TextOrLastResult(StepExecutionContext context, Input<string?>? input)
        {
            var value = input?.GetOrDefault(context);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            return context.GetLastResult() switch
            {
                null => null,
                string text => text,
                var other => other.ToString(),
            };
        }

        /// <summary>A required text input, or a configuration fault naming it.</summary>
        protected static string Required(StepExecutionContext context, Input<string>? input, string name)
        {
            var value = input?.GetOrDefault(context);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new FaultException("INPUT_REQUIRED", "Pipelines", "Configuration", $"The step needs its '{name}' input.");
            }

            return value;
        }

        protected async ValueTask SucceedAsync(StepExecutionContext context, string? result, string outcome = DoneOutcome)
        {
            context.Set(Result, result);
            context.Set(Error, null);
            await context.CompleteStepWithOutcomesAsync(outcome).ConfigureAwait(false);
        }

        protected async ValueTask FailAsync(StepExecutionContext context, string code, string message, string outcome = FailedOutcome)
        {
            var error = $"{code}: {message}";
            context.AddExecutionLogEntry("Failed", error);
            if (OnFailure.GetOrDefault(context) == FailurePolicy.Fault)
            {
                throw new FaultException(code, "Pipelines", "Business", message);
            }

            context.Set(Error, error);
            await context.CompleteStepWithOutcomesAsync(outcome).ConfigureAwait(false);
        }
    }
}
