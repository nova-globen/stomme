#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// The triggers' settings (BL-95 increment (e)), bound from the <c>Pipelines</c> section of THIS host's
    /// configuration root. Schedules run by default (a schedule exists only where an author placed one);
    /// the webhook receiver is a PUBLIC, unauthenticated endpoint and is therefore OFF by default, and even when on
    /// it answers nothing without a secret to verify against.
    /// </summary>
    public sealed class PipelineTriggerOptions
    {
        public const string Section = "Pipelines";

        public ScheduleTriggerOptions Schedules { get; set; } = new();

        public WebhookTriggerOptions Webhooks { get; set; } = new();

        /// <summary>BL-229: <c>Pipelines:Runs</c> - how long a finished run is kept.</summary>
        public RunRetentionOptions Runs { get; set; } = new();

        /// <summary>BL-229: <c>Pipelines:Waits</c> - the Wait step's bounds and how often due waits are picked up.</summary>
        public WaitOptions Waits { get; set; } = new();

        /// <summary>BL-229: <c>Pipelines:Forms</c> - the pages the Form trigger serves.</summary>
        public FormTriggerOptions Forms { get; set; } = new();

        /// <summary>BL-229: <c>Pipelines:Chats</c> - the chat pages the Chat trigger serves.</summary>
        public ChatTriggerOptions Chats { get; set; } = new();

        /// <summary>BL-229: <c>Pipelines:Evaluations</c> - how an evaluation's rows are run.</summary>
        public EvaluationOptions Evaluations { get; set; } = new();

        /// <summary>BL-229: <c>Pipelines:FileTriggers</c> - how the "File added" triggers' folders are looked at.</summary>
        public FileTriggerOptions FileTriggers { get; set; } = new();

        /// <summary>BL-229: <c>Pipelines:EmailTriggers</c> - how the "Email received" triggers' mailboxes are looked at.</summary>
        public EmailTriggerOptions EmailTriggers { get; set; } = new();

        /// <summary>BL-229: <c>Pipelines:TelegramTriggers</c> - how the "Telegram message received" triggers' bots are asked.</summary>
        public TelegramTriggerOptions TelegramTriggers { get; set; } = new();
    }

    /// <summary>
    /// <c>Pipelines:EmailTriggers</c> (n8n's Email Trigger (IMAP)): how often each site's watched mailboxes are looked at,
    /// and the bounds of a look. Nothing to switch on: a trigger reads only a mailbox one of its site's connections names.
    /// </summary>
    public sealed class EmailTriggerOptions
    {
        /// <summary>How often each site's watched mailboxes are looked at - n8n's Email Trigger polls every minute too.</summary>
        public int IntervalSeconds { get; set; } = 60;

        /// <summary>The most messages one look at a mailbox starts runs for; the rest at the next look.</summary>
        public int MaxMessagesPerLook { get; set; } = 25;

        /// <summary>The most bytes of one attachment kept in the pipeline folder; a larger one is named, and said to be skipped.</summary>
        public long MaxAttachmentBytes { get; set; } = 10L * 1024 * 1024;
    }

    /// <summary>
    /// <c>Pipelines:TelegramTriggers</c> (n8n's Telegram Trigger): how often each site's watched bots are asked for their
    /// messages, and the most a look starts runs for. Nothing to switch on: a trigger asks only a bot one of its site's connections
    /// names.
    /// </summary>
    public sealed class TelegramTriggerOptions
    {
        /// <summary>How often each site's watched bots are asked - n8n's trigger is called at once by a webhook; asking is near it.</summary>
        public int IntervalSeconds { get; set; } = 10;

        /// <summary>The most messages one look starts runs for (Telegram answers at most 100); the rest at the next look.</summary>
        public int MaxMessagesPerLook { get; set; } = 25;
    }

    /// <summary>
    /// <c>Pipelines:FileTriggers</c> (n8n's Local File Trigger): how often each site's watched folders are looked at, and
    /// the bounds of a look. Nothing to switch on: a "File added" trigger reads only its own site's pipeline folder.
    /// </summary>
    public sealed class FileTriggerOptions
    {
        /// <summary>How often each site's watched folders are looked at.</summary>
        public int IntervalSeconds { get; set; } = 10;

        /// <summary>A file written this recently waits for the next look - it may still be being written.</summary>
        public int SettleSeconds { get; set; } = 2;

        /// <summary>The most files one look at a folder starts runs for; the rest at the next look.</summary>
        public int MaxRunsPerLook { get; set; } = 25;

        /// <summary>The most files of a watched folder that are watched - the newest.</summary>
        public int MaxFiles { get; set; } = 10_000;
    }

    /// <summary><c>Pipelines:Evaluations</c> (n8n's Evaluations): how an evaluation's rows are run.</summary>
    public sealed class EvaluationOptions
    {
        /// <summary>How often each site's running evaluations are moved on: a finished row noticed, the next one started.</summary>
        public int IntervalSeconds { get; set; } = 5;

        /// <summary>How many of an evaluation's rows run at once (1 to 10); a pipeline that runs one at a time still does.</summary>
        public int RowsAtOnce { get; set; } = 3;

        /// <summary>How long a row's run may take before it is cancelled and the row counted as timed out - a run waiting on a person, say.</summary>
        public int RowTimeoutMinutes { get; set; } = 10;
    }

    /// <summary>
    /// <c>Pipelines:Chats</c> (n8n's Chat trigger): whether the site serves its pipelines' chat pages - public, each message
    /// a run (and, behind an AI agent, a model call) - so off unless the install says so, and their bounds.
    /// </summary>
    public sealed class ChatTriggerOptions
    {
        public bool Enabled { get; set; }

        /// <summary>Requests (a page or a message) per site and caller address per minute; 0 switches the limit off.</summary>
        public int RequestsPerMinute { get; set; } = 20;

        /// <summary>The largest message post read, in bytes.</summary>
        public int MaxBodyBytes { get; set; } = 16 * 1024;
    }

    /// <summary>
    /// <c>Pipelines:Forms</c> (n8n's Form trigger): whether the site serves its pipelines' forms - public pages anyone can
    /// submit, so off unless the install says so - and their bounds.
    /// </summary>
    public sealed class FormTriggerOptions
    {
        public bool Enabled { get; set; }

        /// <summary>Requests (a page or a submission) per site and caller address per minute; 0 switches the limit off.</summary>
        public int RequestsPerMinute { get; set; } = 30;

        /// <summary>The largest submission read, in bytes - its answers; a file field's file is counted apart (<see cref="MaxFileBytes"/>).</summary>
        public int MaxBodyBytes { get; set; } = 64 * 1024;

        /// <summary>The largest file a form's file field takes, in bytes; 0 refuses every file.</summary>
        public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>
        /// BL-229, n8n's multi-page forms: how long a submission waits for its run to reach the next page (a Wait for a form with Next
        /// page) before the person is thanked instead - the run goes on either way. Only a form a next page can follow waits.
        /// </summary>
        public int NextPageWithinSeconds { get; set; } = 15;
    }

    /// <summary><c>Pipelines:Waits</c> (n8n's Wait node).</summary>
    public sealed class WaitOptions
    {
        /// <summary>How often each site's waiting runs are looked at; a due one is resumed within this many seconds.</summary>
        public int IntervalSeconds { get; set; } = 30;

        /// <summary>The longest a run may wait, in days - a wait past it fails the step, saying so.</summary>
        public int MaxDays { get; set; } = 365;
    }

    /// <summary><c>Pipelines:Runs</c> (n8n's execution pruning).</summary>
    public sealed class RunRetentionOptions
    {
        /// <summary>
        /// Days a FINISHED run is kept - with its log and its steps' records - after it last changed; 0, the default, keeps every
        /// run, so an install loses nothing it does not ask to. A run still going is never deleted.
        /// </summary>
        public int KeepDays { get; set; }

        /// <summary>How often each site's old runs are looked for.</summary>
        public int IntervalMinutes { get; set; } = 60;

        /// <summary>How often the pipelines that keep only failed runs have their good runs deleted (PipelineRunSettingsTask).</summary>
        public int SettingsIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// How often a pipeline that runs one at a time, the rest in turn, has its next waiting run started once the run before it
        /// has ended (<see cref="Reliability.PipelineTurnsTask"/>) - the longest a waiting run stands idle; at least two seconds.
        /// </summary>
        public int TurnsIntervalSeconds { get; set; } = 5;
    }

    /// <summary><c>Pipelines:Schedules</c>.</summary>
    public sealed class ScheduleTriggerOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>How often each site's schedules are evaluated; a cron field's resolution is a minute, so the default is one.</summary>
        public int IntervalSeconds { get; set; } = 60;
    }

    /// <summary><c>Pipelines:Webhooks</c>.</summary>
    public sealed class WebhookTriggerOptions
    {
        public bool Enabled { get; set; }

        /// <summary>
        /// The HMAC-SHA256 secret every webhook request is signed with (<c>X-Signature-256: sha256=&lt;hex&gt;</c> over the raw body). From the broker, never a row or the UI.
        /// A webhook with a secret of its own (<see cref="PipelineWebhookSecrets"/>) is verified under that instead.
        /// </summary>
        public string? Secret { get; set; }

        /// <summary>
        /// A file holding the secret - a mounted secret, the form a deployment keeps secrets in (as <c>Backup:Schedule:PassphraseFile</c>).
        /// When set it wins over <see cref="Secret"/>, read at each call so a rotated file takes effect at once.
        /// </summary>
        public string? SecretFile { get; set; }

        /// <summary>
        /// The secret calls are verified with: the file's content when <see cref="SecretFile"/> is set (trailing line breaks
        /// trimmed, nothing else), else <see cref="Secret"/>. Null, with the reason, when there is none - a file named but
        /// missing, unreadable or empty included - and then every call is refused: never unsigned.
        /// </summary>
        public string? EffectiveSecret(out string? problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(SecretFile))
            {
                if (string.IsNullOrWhiteSpace(Secret))
                {
                    problem = "Pipelines:Webhooks has no Secret or SecretFile.";
                    return null;
                }

                return Secret;
            }

            try
            {
                var secret = System.IO.File.ReadAllText(SecretFile).TrimEnd('\r', '\n');
                if (secret.Length > 0)
                {
                    return secret;
                }

                problem = $"Pipelines:Webhooks:SecretFile names '{SecretFile}', which is empty.";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                problem = $"Pipelines:Webhooks:SecretFile names '{SecretFile}', which cannot be read: {ex.Message}";
            }

            return null;
        }

        /// <summary>Requests per minute per site and caller address before the receiver answers 429.</summary>
        public int RequestsPerMinute { get; set; } = 60;

        /// <summary>
        /// How long a call to a webhook that answers from the pipeline waits for its Respond to webhook step before it is
        /// answered 202 and the run goes on without it (1 to 120 seconds).
        /// </summary>
        public int AnswerWithinSeconds { get; set; } = 30;

        /// <summary>The largest body the receiver reads.</summary>
        public int MaxBodyBytes { get; set; } = 1024 * 1024;

        /// <summary>
        /// The callers every webhook admits: literal IP addresses or CIDR ranges (the predecessor platform's per-endpoint allowlist, absorbed as
        /// configuration beside the secret). Empty admits any caller. A caller outside it is refused before its body is read.
        /// </summary>
        public List<string> AllowedCallers { get; set; } = new();

        /// <summary>Seconds within which a repeat of an AUTHENTIC delivery is dropped (answered 202, not dispatched); 0 turns it off.</summary>
        public int DeduplicationSeconds { get; set; }

        /// <summary>A request header carrying the delivery's own id (e.g. <c>X-Request-Id</c>) to recognise a repeat by; empty: the body's SHA-256.</summary>
        public string? DeduplicationHeader { get; set; }

        /// <summary>Per webhook NAME, overrides of <see cref="AllowedCallers"/>, <see cref="DeduplicationSeconds"/> and <see cref="DeduplicationHeader"/>.</summary>
        public Dictionary<string, WebhookEndpointOptions> Endpoints { get; set; } = new();

        /// <summary>The settings that apply to one (normalised) webhook name: its endpoint's where it sets them, these otherwise.</summary>
        public WebhookEndpointSettings For(string name)
        {
            var endpoint = Endpoints.FirstOrDefault(pair => string.Equals(pair.Key?.Trim(), name, StringComparison.OrdinalIgnoreCase)).Value;
            return new WebhookEndpointSettings(
                endpoint?.AllowedCallers is { Count: > 0 } callers ? callers : AllowedCallers,
                endpoint?.DeduplicationSeconds ?? DeduplicationSeconds,
                string.IsNullOrWhiteSpace(endpoint?.DeduplicationHeader) ? DeduplicationHeader : endpoint!.DeduplicationHeader);
        }
    }

    /// <summary><c>Pipelines:Webhooks:Endpoints:{name}</c> - what one webhook name sets for itself; anything left unset inherits.</summary>
    public sealed class WebhookEndpointOptions
    {
        public List<string>? AllowedCallers { get; set; }

        public int? DeduplicationSeconds { get; set; }

        public string? DeduplicationHeader { get; set; }
    }

    /// <summary>The effective settings of one webhook name.</summary>
    public sealed record WebhookEndpointSettings(IReadOnlyList<string> AllowedCallers, int DeduplicationSeconds, string? DeduplicationHeader);
}
