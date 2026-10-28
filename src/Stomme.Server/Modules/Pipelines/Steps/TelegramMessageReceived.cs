#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The stimulus a "Telegram message received" trigger is indexed by: the connection - the bot - alone. Telegram hands each
    /// update once (getUpdates forgets what it has handed on), so a bot has ONE watcher, and every pipeline on it shares it.
    /// </summary>
    public sealed record TelegramMessageStimulus(string Connection);

    /// <summary>
    /// n8n's Telegram Trigger, as an engine TRIGGER (BL-229): a published pipeline that starts with it is indexed under one of the
    /// site's Telegram connections, and <c>Triggers/WatchedTelegramBots.cs</c> asks the bot for its updates through the connection -
    /// the token in the site's secret store, the host's egress, never the pipeline's - starting a run for each message or channel
    /// post that came since it last asked, the message its result (<see cref="PipelineTelegramEvent"/>). n8n's trigger takes them by
    /// a webhook Telegram calls; here they are asked for, so a bot needs no address the internet can reach - and a bot with a
    /// webhook set gets none here, which the log says. Messages waiting when the pipeline is published start nothing. A
    /// connection that is no id is refused at the save.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline when one of the site's Telegram bots gets a message - n8n's Telegram Trigger.", DisplayName = "Telegram message received")]
    public class TelegramMessageReceived : Trigger<string>
    {
        public const string MessageInputKey = "__TelegramMessage";

        [Input(DisplayName = "Connection", UIHint = PipelineInputHints.ConnectionPicker, Description = "One of the site's Telegram connections (Admin > Connections) - the bot whose messages start the pipeline.", Order = 1)]
        [PipelineConnectorTypes("telegram")]
        public Input<string> Connection { get; set; } = null!;

        /// <summary>Why a connection cannot be the bot a trigger watches - null when it can.</summary>
        public static string? ConnectionProblem(string? connection) =>
            Guid.TryParse(connection?.Trim(), out _) ? null : $"'{connection}' is not a connection id - choose one of the site's Telegram connections.";

        /// <summary>The stimulus for a connection, as it is indexed at publish - or the refusal, thrown.</summary>
        public static TelegramMessageStimulus Stimulus(string? connection)
        {
            if (ConnectionProblem(connection) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            return new TelegramMessageStimulus(Guid.Parse(connection!.Trim()).ToString("D"));
        }

        protected override object GetTriggerPayload(TriggerIndexingContext context) => Stimulus(context.Get(Connection));

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself.
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            context.SetResult(context.TryGetPipelineInput<string>(MessageInputKey, out var message) ? message : "");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}
