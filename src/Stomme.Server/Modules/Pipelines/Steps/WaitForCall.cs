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
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What a run waiting for a call keeps on its bookmark: the SHA-256 of its token, never the token.</summary>
    public sealed record CallStimulus(string TokenHash);

    /// <summary>
    /// Wait for a call (n8n's Wait node resumed "on webhook call"): the run pauses until another system POSTs to the
    /// run's <see cref="CallAddress"/>, and goes on down Called with the call's body as the result - or down Timed out when
    /// "Time out after" was given and passed (<see cref="DueWaitsTask"/> watches the time). Durable like <see cref="Wait"/>: the
    /// run is stored and holds no worker. The call reaches the run through the engine's own stimulus path, so a call that comes
    /// before the run gets here is queued for a minute, and two waits for one address take one call each.
    /// </summary>
    [FlowNode(PipelineCalls.Called, PipelineCalls.TimedOut)]
    [Step("Stomme", "Control Flow", "Pause the run until its call address is called (POST); the call's body is the result. Durable: the run is stored and holds no worker.", DisplayName = "Wait for a call")]
    public class WaitForCall : Step<string>
    {
        /// <summary>Where the bookmark keeps the moment the wait times out.</summary>
        public const string ExpiresAtKey = "stomme:call:expiresAt";

        [Input(DisplayName = "Time out after", Description = "How long to wait for the call: 10 minutes, 2 hours, 1 day. When it passes with no call, the run goes on down Timed out. Empty: it waits until called.", Order = 1)]
        public Input<string?>? TimeOutAfter { get; set; }

        protected override ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var token = CallAddress.TokenOf(context)
                ?? throw new FaultException("CALL_NO_ADDRESS", "Pipelines", "Configuration", "Place a 'Call address' step before 'Wait for a call': it makes the address another system calls.");

            var now = context.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;
            DateTimeOffset? expires = null;
            if (TimeOutAfter?.GetOrDefault(context) is { } within && !string.IsNullOrWhiteSpace(within))
            {
                if (!PipelineWaits.TryParseDuration(within, out var span, out var error))
                {
                    throw new FaultException("CALL_INVALID", "Pipelines", "Configuration", "Time out after: " + error);
                }

                var maxDays = Math.Max(1, context.GetService<IOptions<PipelineTriggerOptions>>()?.Value.Waits.MaxDays ?? new WaitOptions().MaxDays);
                if (span > TimeSpan.FromDays(maxDays))
                {
                    throw new FaultException("CALL_TOO_LONG", "Pipelines", "Configuration", $"A wait for a call can be at most Pipelines:Waits:MaxDays ({maxDays}) days.");
                }

                expires = now + span;
            }

            var metadata = expires is null ? null : new Dictionary<string, string> { [ExpiresAtKey] = expires.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) };
            context.CreateBookmark(new CallStimulus(PipelineCalls.Hash(token)), ResumeAsync, includeStepInstanceId: false, metadata);
            context.AddExecutionLogEntry("Waiting for a call", expires is null ? "Waits until the run's call address is called." : $"Waits until the run's call address is called, or {expires.Value.UtcDateTime:yyyy-MM-dd HH:mm} UTC.");
            return ValueTask.CompletedTask;
        }

        // Called by name when the bookmark is resumed - by a call, or by the task that watches the time.
        private async ValueTask ResumeAsync(StepExecutionContext context)
        {
            if (context.PipelineInput.TryGetValue(PipelineCalls.TimedOutKey, out var timedOut) && Text(timedOut) == "true")
            {
                context.Set(Result, null);
                context.AddExecutionLogEntry(PipelineCalls.TimedOut, "No call came in time.");
                await context.CompleteStepWithOutcomesAsync(PipelineCalls.TimedOut).ConfigureAwait(false);
                return;
            }

            var body = context.PipelineInput.TryGetValue(PipelineCalls.BodyKey, out var sent) ? Text(sent) : string.Empty;
            var contentType = context.PipelineInput.TryGetValue(PipelineCalls.ContentTypeKey, out var type) ? Text(type) : string.Empty;
            context.Set(Result, body);
            context.AddExecutionLogEntry(PipelineCalls.Called, $"Called with {body.Length} characters{(contentType.Length == 0 ? string.Empty : " of " + contentType)}.");
            await context.CompleteStepWithOutcomesAsync(PipelineCalls.Called).ConfigureAwait(false);
        }

        private static string Text(object? value) => value switch
        {
            null => string.Empty,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            bool flag => flag ? "true" : "false",
            var other => other.ToString() ?? string.Empty,
        };

        /// <summary>When a waiting call's bookmark times out, from its metadata; null when it does not.</summary>
        public static DateTimeOffset? ExpiresOf(IDictionary<string, string>? metadata) =>
            metadata is not null && metadata.TryGetValue(ExpiresAtKey, out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires)
                ? expires
                : null;

        /// <summary>What a call is resumed with.</summary>
        public static IDictionary<string, object> CallInput(string body, string? contentType) => new Dictionary<string, object>
        {
            [PipelineCalls.BodyKey] = body,
            [PipelineCalls.ContentTypeKey] = contentType ?? string.Empty,
        };

        /// <summary>What a time-out is resumed with.</summary>
        public static IDictionary<string, object> TimedOutInput() => new Dictionary<string, object> { [PipelineCalls.TimedOutKey] = "true" };
    }
}
