#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>A published chat as the site serves it: what its trigger says, literally.</summary>
    public sealed record PipelineChatSpec(string Name, string Title, string? Welcome);

    /// <summary>One line of a visitor's conversation: who said it and what.</summary>
    public sealed record PipelineChatLine(string Role, string Text);

    /// <summary>
    /// BL-229 - the site's chats (n8n's Chat Trigger), the site's side: the chat a published pipeline offers under a name (from its
    /// trigger step, as the engine indexed it); a visitor's message sent as the trigger's stimulus, which RUNS the pipeline at once
    /// (the stimulus sender is in-process) and whose answer is the result of the last step its run completes; and each visitor's
    /// conversation, kept in the engine's key-value store under the site, the chat and the visitor's session (the newest
    /// <see cref="MaxLines"/> lines). A visitor never sees why a run failed - an incident's text is the site's, not theirs.
    /// </summary>
    public sealed class PipelineChatReceiver(
        ITriggerStore triggers,
        IStimulusHasher hasher,
        IPipelineDefinitionStore definitions,
        IStimulusSender sender,
        IStepExecutionStore steps,
        IKeyValueStore keyValues,
        ITenantAccessor tenants)
    {
        public const int MaxLines = 100;

        public const string KeyPrefix = "pipelines:chat:";

        public const string NoAnswer = "Sorry - there is no answer to that just now.";

        /// <summary>The chat under the name, as its trigger says it; null when no published pipeline on the site has one.</summary>
        public async Task<PipelineChatSpec?> FindAsync(string name, CancellationToken cancellationToken)
        {
            var stored = await triggers.FindAsync(new TriggerFilter { Name = ChatTrigger.StepTypeName, Hash = hasher.Hash(ChatTrigger.StepTypeName, new ChatStimulus(name)) }, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return null;
            }

            var definition = await definitions.FindAsync(new PipelineDefinitionFilter { Id = stored.PipelineDefinitionVersionId }, cancellationToken).ConfigureAwait(false);
            var step = definition is null ? null : PipelineGraphCodec.ToGraph(definition.StringData).Steps.FirstOrDefault(each => each.Type == ChatTrigger.StepTypeName && each.Id == stored.StepId);
            if (step is null)
            {
                return null;
            }

            string? Literal(string input) => step.Inputs.TryGetValue(input, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
            return new PipelineChatSpec(name, Literal(nameof(ChatTrigger.Title)) ?? name, Literal(nameof(ChatTrigger.Welcome)));
        }

        /// <summary>Runs the chat's pipeline with the message and gives its answer - what the visitor may see.</summary>
        public async Task<string> AskAsync(string name, string session, string message, CancellationToken cancellationToken)
        {
            var sent = await sender.SendAsync(ChatTrigger.StepTypeName, new ChatStimulus(name), new StimulusMetadata
            {
                CorrelationId = "chat:" + session,
                Input = new Dictionary<string, object> { [ChatTrigger.MessageInputKey] = message, [ChatTrigger.SessionInputKey] = session },
            }, cancellationToken).ConfigureAwait(false);
            var run = sent.PipelineInstanceResponses.FirstOrDefault();
            if (run is null || run.Incidents.Count > 0 || run.SubStatus != PipelineSubStatus.Finished)
            {
                return run is { SubStatus: PipelineSubStatus.Suspended } ? "Thank you - the answer will take a while." : NoAnswer;
            }

            var answer = await PipelineRunResults.LastResultAsync(steps, run.PipelineInstanceId, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(answer) ? NoAnswer : answer;
        }

        public async Task<List<PipelineChatLine>> TranscriptAsync(string name, string session, CancellationToken cancellationToken)
        {
            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = Key(name, session) }, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<List<PipelineChatLine>>(stored?.SerializedValue ?? "[]") ?? new List<PipelineChatLine>();
            }
            catch (JsonException)
            {
                return new List<PipelineChatLine>();
            }
        }

        public async Task AppendAsync(string name, string session, IEnumerable<PipelineChatLine> lines, CancellationToken cancellationToken)
        {
            var transcript = await TranscriptAsync(name, session, cancellationToken).ConfigureAwait(false);
            transcript.AddRange(lines);
            var kept = transcript.Skip(Math.Max(0, transcript.Count - MaxLines)).ToList();
            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = Key(name, session), SerializedValue = JsonSerializer.Serialize(kept) }, cancellationToken).ConfigureAwait(false);
        }

        public Task ForgetAsync(string name, string session, CancellationToken cancellationToken) =>
            keyValues.DeleteAsync(Key(name, session), cancellationToken);

        private string Key(string name, string session) => KeyPrefix + tenants.TenantId + ":" + name + ":" + session;

        /// <summary>A new visitor's session: random, so one visitor cannot guess another's conversation.</summary>
        public static string NewSession() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        /// <summary>Whether a cookie's value can be a session this receiver made (32 hex characters).</summary>
        public static bool IsSession(string? value) =>
            value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// The chat's page: one standalone document, as a form's - every text encoded (a visitor's message and the pipeline's answer
    /// are text, never markup), nothing loaded, no script (a message is a plain form post, the page shown again after it), the
    /// browser's own colours; the policy is the form page's.
    /// </summary>
    public static class PipelineChatPage
    {
        public const string PolicyName = "pipelines-chats";

        public const string NewConversationField = "__new";

        private const string Style = """
            :root { color-scheme: light dark; }
            body { margin: 0; font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background: Canvas; color: CanvasText; }
            main { box-sizing: border-box; max-width: 40rem; margin: 0 auto; padding: 1.5rem 16px; }
            h1 { font-size: 1.4rem; margin: 0 0 1rem; }
            ol { list-style: none; margin: 0 0 1rem; padding: 0; display: flex; flex-direction: column; gap: 0.5rem; }
            li { padding: 0.5rem 0.75rem; border-radius: 0.5rem; border: 1px solid GrayText; white-space: pre-wrap; overflow-wrap: anywhere; max-width: 85%; }
            li.user { align-self: flex-end; background: Field; color: FieldText; }
            li.assistant { align-self: flex-start; }
            .who { display: block; font-size: 0.75rem; color: GrayText; margin-bottom: 0.15rem; }
            textarea { box-sizing: border-box; width: 100%; min-height: 4.5rem; font: inherit; padding: 0.5rem; border: 1px solid GrayText; border-radius: 0.375rem; background: Field; color: FieldText; }
            .actions { display: flex; gap: 0.5rem; margin-top: 0.5rem; }
            button { font: inherit; font-weight: 600; padding: 0.45rem 1.1rem; border: 1px solid ButtonBorder; border-radius: 0.375rem; background: ButtonFace; color: ButtonText; cursor: pointer; }
            .error { font-weight: 600; margin: 0 0 0.5rem; }
            .hp { position: absolute; left: -10000px; width: 1px; height: 1px; overflow: hidden; }
            """;

        public static string Render(PipelineChatSpec chat, IReadOnlyList<PipelineChatLine> transcript, string? error = null, string? draft = null)
        {
            var html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"robots\" content=\"noindex\"><title>")
                .Append(Encode(chat.Title)).Append("</title><style>").Append(Style).Append("</style></head><body><main>");
            html.Append("<h1>").Append(Encode(chat.Title)).Append("</h1>");
            html.Append("<ol aria-live=\"polite\">");
            if (!string.IsNullOrWhiteSpace(chat.Welcome))
            {
                Line(html, new PipelineChatLine("assistant", chat.Welcome));
            }

            foreach (var line in transcript)
            {
                Line(html, line);
            }

            html.Append("</ol>");
            html.Append("<form method=\"post\" accept-charset=\"utf-8\">");
            if (error is not null)
            {
                html.Append("<p class=\"error\" role=\"alert\" id=\"message-error\">").Append(Encode(error)).Append("</p>");
            }

            html.Append("<label for=\"message\" class=\"hp\">Your message</label><textarea id=\"message\" name=\"message\" required maxlength=\"")
                .Append(PipelineChatOptionsDefaults.MaxMessageLength).Append("\" placeholder=\"Write a message\"")
                .Append(error is null ? "" : " aria-invalid=\"true\" aria-describedby=\"message-error\"").Append('>').Append(Encode(draft)).Append("</textarea>");
            html.Append("<p class=\"hp\" aria-hidden=\"true\"><label>Leave this empty <input type=\"text\" name=\"").Append(PipelineFormPage.HoneypotField)
                .Append("\" tabindex=\"-1\" autocomplete=\"off\"></label></p>");
            html.Append("<div class=\"actions\"><button type=\"submit\">Send</button>")
                .Append("<button type=\"submit\" name=\"").Append(NewConversationField).Append("\" value=\"1\" formnovalidate>New conversation</button></div>");
            html.Append("</form></main></body></html>");
            return html.ToString();
        }

        private static void Line(StringBuilder html, PipelineChatLine line)
        {
            var user = line.Role == "user";
            html.Append("<li class=\"").Append(user ? "user" : "assistant").Append("\"><span class=\"who\">").Append(user ? "You" : "Reply").Append("</span>")
                .Append(Encode(line.Text)).Append("</li>");
        }

        private static string Encode(string? text) => HtmlEncoder.Default.Encode(text ?? "");
    }

    /// <summary>The longest message a visitor may send, also in the page's textarea.</summary>
    public static class PipelineChatOptionsDefaults
    {
        public const int MaxMessageLength = 2000;
    }
}
