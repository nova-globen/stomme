#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>The stimulus a chat is indexed and messaged by: its name on the site. Its property is no webhook's or form's.</summary>
    public sealed record ChatStimulus(string Chat);

    /// <summary>
    /// n8n's Chat Trigger, hosted by the site: a page at <c>api/Pipelines/chats/{name}</c> (behind
    /// <c>Pipelines:Chats:Enabled</c>, rate-limited, anonymous - a chat is for the site's visitors) where a visitor talks to the
    /// pipeline. Each message RUNS it - the message this step's result, the visitor's conversation its <see cref="Session"/> - and
    /// the reply shown is the result of the last step the run completes; the conversation is kept per visitor. Give the session
    /// to an <see cref="AiAgent"/> as its memory key and the agent remembers the conversation too.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline when a visitor sends a message on the chat page the site shows for it; the reply is the last step's result.", DisplayName = "Chat message received")]
    public class ChatTrigger : Trigger<string>
    {
        public const string StepTypeName = "Stomme.ChatTrigger";
        public const string MessageInputKey = "__ChatMessage";
        public const string SessionInputKey = "__ChatSession";

        [Input(DisplayName = "Name", Description = "The chat's name: the last segment of its address, api/Pipelines/chats/{name}. Letters, digits, '-', '_' and '.'.", Order = 1)]
        public Input<string> ChatName { get; set; } = null!;

        [Input(Description = "The chat page's heading.", Order = 2)]
        public Input<string?>? Title { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "What the page says first, before the visitor writes.", Order = 3)]
        public Input<string?>? Welcome { get; set; }

        [Output(Description = "The visitor's conversation - the same for every message in it. Give it to an AI agent as its memory key.")]
        public Output<string>? Session { get; set; }

        protected override object GetTriggerPayload(TriggerIndexingContext context)
        {
            var name = context.Get(ChatName);
            if (!Webhook.IsValidName(name))
            {
                throw new InvalidOperationException($"'{name}' is not a chat name (letters, digits, '-', '_', '.', at most 100 characters).");
            }

            return new ChatStimulus(Webhook.Normalize(name!));
        }

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself.
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            context.Set(Session, context.TryGetPipelineInput<string>(SessionInputKey, out var session) ? session : "");
            context.SetResult(context.TryGetPipelineInput<string>(MessageInputKey, out var message) ? message : "");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}
