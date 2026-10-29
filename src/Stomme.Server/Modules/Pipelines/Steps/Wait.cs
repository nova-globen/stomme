#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>The moment a waiting run is due, as its bookmark keeps it (ISO 8601, UTC).</summary>
    public sealed record WaitStimulus(string ResumeAt);

    /// <summary>
    /// Wait (n8n's Wait node): the run pauses for a while, or until a moment, DURABLY - the step leaves a bookmark that
    /// carries when it is due, the run is stored and holds no worker (unlike <see cref="Delay"/>, which holds one and is bounded
    /// to minutes), and <see cref="DueWaitsTask"/> picks it up again when the moment comes - across restarts, as the run is in
    /// the store. A moment already past goes straight on.
    /// </summary>
    /// <remarks>
    /// "For" reads a duration the way a person writes one - <c>30 minutes</c>, <c>2 hours</c>, <c>1 day</c>, <c>1 week</c>,
    /// <c>45s</c> - or as days.hours:minutes:seconds (<c>1.12:00:00</c>); "Until" a moment, <c>2026-12-24T09:00:00Z</c> (a moment
    /// with no offset is UTC). Until wins when both are given. A wait longer than <c>Pipelines:Waits:MaxDays</c> fails the step,
    /// saying so.
    /// </remarks>
    [Step("Stomme", "Control Flow", "Pause the run for a while, or until a moment - durably: the run is stored and picked up again when it is due, holding no worker.", DisplayName = "Wait")]
    public class Wait : CodeStep
    {
        /// <summary>The name the engine keeps the bookmark under (the step's namespace and class name).</summary>
        public const string StepTypeName = PipelineWaits.StepType;

        [Input(DisplayName = "For", Description = "How long to wait: 30 minutes, 2 hours, 1 day, 1 week - or 1.12:00:00 (a day and a half).", Order = 1)]
        public Input<string?>? For { get; set; }

        [Input(DisplayName = "Until", Description = "The moment to wait until, e.g. 2026-12-24T09:00:00Z (UTC when no offset is given). When given, For is not read.", Order = 2)]
        public Input<string?>? Until { get; set; }

        protected override void Execute(StepExecutionContext context)
        {
            var now = context.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;
            var maxDays = Math.Max(1, context.GetService<IOptions<PipelineTriggerOptions>>()?.Value.Waits.MaxDays ?? new WaitOptions().MaxDays);
            var resumeAt = PipelineWaits.ResumeAt(Until?.GetOrDefault(context), For?.GetOrDefault(context), now, out var error);
            if (resumeAt is null)
            {
                throw new FaultException("WAIT_INVALID", "Pipelines", "Configuration", error!);
            }

            if (resumeAt.Value - now > TimeSpan.FromDays(maxDays))
            {
                throw new FaultException("WAIT_TOO_LONG", "Pipelines", "Configuration", $"A wait can be at most Pipelines:Waits:MaxDays ({maxDays}) days; this one ends {resumeAt.Value.UtcDateTime:yyyy-MM-dd HH:mm} UTC.");
            }

            if (resumeAt.Value <= now)
            {
                context.AddExecutionLogEntry("Waited", "The moment has passed; the run goes on.");
                return; // no bookmark: the step completes
            }

            context.CreateBookmark(new WaitStimulus(resumeAt.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ResumeAsync);
            context.AddExecutionLogEntry("Waiting", $"Waits until {resumeAt.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC.");
        }

        // Called by name when the bookmark is resumed (the bookmark keeps the method's name; the engine finds it on the step).
        private async ValueTask ResumeAsync(StepExecutionContext context)
        {
            context.AddExecutionLogEntry("Resumed", "The wait is over; the run goes on.");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }

        /// <summary>The moment a stored bookmark's payload says it is due: typed when the store kept the type, else read from JSON.</summary>
        public static DateTimeOffset? DueOf(object? payload)
        {
            var text = payload switch
            {
                WaitStimulus stimulus => stimulus.ResumeAt,
                JsonElement { ValueKind: JsonValueKind.Object } element => element.TryGetProperty(nameof(WaitStimulus.ResumeAt), out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()
                    : element.TryGetProperty("resumeAt", out var camel) && camel.ValueKind == JsonValueKind.String ? camel.GetString() : null,
                IDictionary<string, object> dictionary => dictionary.TryGetValue(nameof(WaitStimulus.ResumeAt), out var value) ? value?.ToString() : null,
                _ => null,
            };

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var due) ? due : null;
        }
    }
}
