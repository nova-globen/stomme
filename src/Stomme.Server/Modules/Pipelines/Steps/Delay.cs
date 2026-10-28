#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// Pause the run (the predecessor platform's ControlFlow Delay, with its bounded-flow ceiling kept as <c>Pipelines:MaxDelaySeconds</c>).
    /// The pause holds the worker; a durable timer that suspends the run and resumes it later is the triggers
    /// increment's (e), where scheduling lives.
    /// </summary>
    [Step("Stomme", "Control Flow", "Pause the run for a number of seconds (bounded by Pipelines:MaxDelaySeconds).")]
    public class Delay : CodeStep
    {
        [Input(Description = "How long to pause.", DefaultValue = 1)]
        public Input<int> Seconds { get; set; } = new(new Literal<int>(1));

        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var seconds = Seconds.GetOrDefault(context);
            var max = context.GetService<IOptions<PipelineStepOptions>>()?.Value.MaxDelaySeconds ?? new PipelineStepOptions().MaxDelaySeconds;
            if (seconds < 0 || seconds > max)
            {
                throw new FaultException("DELAY_OUT_OF_BOUNDS", "Pipelines", "Configuration", $"A delay must be between 0 and Pipelines:MaxDelaySeconds ({max}) seconds; this one is {seconds}.");
            }

            if (seconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), context.CancellationToken).ConfigureAwait(false);
            }
        }
    }
}
