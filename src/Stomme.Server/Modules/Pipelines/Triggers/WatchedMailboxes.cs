#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Common.RecurringTasks;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Models;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Infrastructure;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// The site's watched mailboxes (BL-229, n8n's Email Trigger (IMAP)): where each "Email received" trigger's mailbox was when it
    /// was last looked at - its UIDVALIDITY and the last UID handed on - and the look that starts the pipelines for each message
    /// since. Pipelines watching a mailbox the same way share one record (one stimulus hash), kept in the engine's key-value
    /// store under the SITE's key and the hash. A look reads, through the connection (<see cref="IConnectorExtractionService"/>:
    /// the connector, the credentials from the site's secret store, the host's egress - the pipeline never holds them), the
    /// messages with a UID after the last, at most <c>MaxMessagesPerLook</c>, oldest first; records the highest UID BEFORE
    /// starting runs, so a message starts its runs once even when a look fails part way; keeps each attachment in the site's
    /// pipeline folder when the trigger says so; starts the pipelines indexed under the hash, one run a message; and then, when
    /// the trigger says so, marks those messages read - only those, never one read past the cap. A mailbox made again (another
    /// UIDVALIDITY) is anchored afresh, its messages starting nothing, and said in the log. A Gmail connection speaks the same
    /// operations (status, read, mark_read), its "UIDs" the points in a label's history its messages were added at - so a
    /// message read is marked by its Gmail id as well, which the mark is handed beside the UIDs.
    /// </summary>
    public sealed class WatchedMailboxes(
        IKeyValueStore keyValues,
        IStimulusSender stimulusSender,
        ISystemClock clock,
        ITenantAccessor tenantAccessor,
        IServiceProvider services,
        IOptions<PipelineTriggerOptions> options,
        IOptions<PipelineStepOptions> stepOptions,
        ILogger<WatchedMailboxes> logger)
    {
        public const string SeenKeyPrefix = "pipelines:mail:watched:";

        /// <summary>Where a site keeps where a watched mailbox was: its tenant, then the trigger's hash.</summary>
        public static string SeenKey(string? tenantId, string hash) => SeenKeyPrefix + tenantId + ":" + hash;

        /// <summary>Where a mailbox was: its UIDVALIDITY and the last UID handed on.</summary>
        public sealed record Mark(long UidValidity, long LastUid);

        /// <summary>
        /// The first look at a mailbox, when a pipeline watching it is published: its UIDVALIDITY and next UID are recorded, so the
        /// messages there then start nothing and the next one to arrive starts a run though no look has come round yet. A mailbox
        /// already watched the same way keeps its record. False when the mailbox could not be read - said in the log, and the
        /// next look records it instead.
        /// </summary>
        public async Task<bool> StartWatchingAsync(string hash, EmailReceivedStimulus stimulus, CancellationToken ct)
        {
            var key = SeenKey(tenantAccessor.TenantId, hash);
            if (await keyValues.FindAsync(new KeyValueFilter { Key = key }, ct).ConfigureAwait(false) is not null)
            {
                return true;
            }

            var status = await ExtractAsync(stimulus, "status", new Dictionary<string, string>(), 1, ct).ConfigureAwait(false);
            if (status?.FirstOrDefault() is not { } record)
            {
                return false;
            }

            var mark = new Mark(Long(record, "uid_validity"), Math.Max(0, Long(record, "uid_next") - 1));
            await SaveAsync(key, mark, ct).ConfigureAwait(false);
            logger.LogInformation("Mailbox '{Mailbox}' of connection {Connection} on tenant {Tenant} is watched from UID {Uid} on; the messages there start nothing.",
                stimulus.Mailbox, stimulus.Connection, tenantAccessor.TenantId, mark.LastUid);
            return true;
        }

        /// <summary>A look at a watched mailbox: the pipelines started for each message since the last look; the first look only records.</summary>
        public async Task LookAsync(string hash, EmailReceivedStimulus stimulus, CancellationToken ct)
        {
            var tenant = tenantAccessor.TenantId;
            var key = SeenKey(tenant, hash);
            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = key }, ct).ConfigureAwait(false);
            if (Read(stored?.SerializedValue) is not { } mark)
            {
                // Its publish did not record it (the server could not be reached then, or the record was forgotten).
                await StartWatchingAsync(hash, stimulus, ct).ConfigureAwait(false);
                return;
            }

            var settings = options.Value.EmailTriggers;
            var records = await ExtractAsync(stimulus, "read", new Dictionary<string, string>
            {
                ["after_uid"] = mark.LastUid.ToString(CultureInfo.InvariantCulture),
                ["uid_validity"] = mark.UidValidity.ToString(CultureInfo.InvariantCulture),
                ["attachments"] = stimulus.KeepAttachments ? "content" : "names",
                ["attachment_max_bytes"] = Math.Max(0, settings.MaxAttachmentBytes).ToString(CultureInfo.InvariantCulture),
            }, Math.Max(1, settings.MaxMessagesPerLook), ct).ConfigureAwait(false);
            if (records is null || records.Count == 0)
            {
                return;
            }

            if (records[0].Fields.TryGetValue("renumbered", out var renumbered) && renumbered is true)
            {
                var anew = new Mark(Long(records[0], "uid_validity"), Math.Max(0, Long(records[0], "uid_next") - 1));
                await SaveAsync(key, anew, ct).ConfigureAwait(false);
                logger.LogWarning("Mailbox '{Mailbox}' of connection {Connection} on tenant {Tenant} was made again (UIDVALIDITY {Old}, now {New}): it is watched afresh, its messages starting nothing.",
                    stimulus.Mailbox, stimulus.Connection, tenant, mark.UidValidity, anew.UidValidity);
                return;
            }

            var messages = records.Where(record => Long(record, "uid") > mark.LastUid).OrderBy(record => Long(record, "uid")).ToList();
            if (messages.Count == 0)
            {
                return;
            }

            // Recorded before any run starts: a message starts its runs once, even when this look fails part way.
            await SaveAsync(key, mark with { LastUid = Long(messages[^1], "uid") }, ct).ConfigureAwait(false);
            var handedOn = new List<long>();
            var handedOnIds = new List<string>();
            foreach (var message in messages)
            {
                var uid = Long(message, "uid");
                try
                {
                    var written = Write(stimulus.Mailbox, message, stimulus.KeepAttachments ? Keep : null);
                    var result = await stimulusSender.SendAsync(hash, new StimulusMetadata
                    {
                        Input = new Dictionary<string, object> { [EmailReceived.MessageInputKey] = written },
                    }, ct).ConfigureAwait(false);
                    handedOn.Add(uid);
                    if (Text(message, "id") is { Length: > 0 } id)
                    {
                        handedOnIds.Add(id); // a Gmail message marks read by its id, not by the point it was added at
                    }
                    logger.LogInformation("Message {Uid} in '{Mailbox}' on tenant {Tenant} started {Count} pipeline(s).", uid, stimulus.Mailbox, tenant, result.PipelineInstanceResponses.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Message {Uid} in '{Mailbox}' on tenant {Tenant} could not start its pipelines: {Error}", uid, stimulus.Mailbox, tenant, ex.Message);
                }
            }

            if (stimulus.MarkRead && handedOn.Count > 0)
            {
                var marking = new Dictionary<string, string> { ["uids"] = string.Join(",", handedOn) };
                if (handedOnIds.Count > 0)
                {
                    marking["ids"] = string.Join(",", handedOnIds);
                }

                await ExtractAsync(stimulus, "mark_read", marking, 1, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Forgets every mailbox of the site no trigger watches any more, so publishing one again starts afresh.</summary>
        public async Task ForgetAllButAsync(IReadOnlyCollection<string> watchedHashes, CancellationToken ct)
        {
            var tenant = tenantAccessor.TenantId;
            var watched = watchedHashes.Select(hash => SeenKey(tenant, hash)).ToHashSet(StringComparer.Ordinal);
            var kept = await keyValues.FindManyAsync(new KeyValueFilter { Key = SeenKeyPrefix + tenant + ":", StartsWith = true }, ct).ConfigureAwait(false);
            foreach (var stale in kept.Where(pair => !watched.Contains(pair.Key)))
            {
                await keyValues.DeleteAsync(stale.Key, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The message as the pipeline reads it: the connector's fields under the editor's names, each attachment kept by
        /// <paramref name="keep"/> - its path in the site's pipeline folder - when there is one to keep it, named only when not (the
        /// editor's fetch of a test message keeps nothing). The content is never in what is written.
        /// </summary>
        internal static string Write(string mailbox, ConnectorRecord record, Func<long, string, byte[], string>? keep)
        {
            var uid = Long(record, "uid");
            var attachments = new JsonArray();
            if (Text(record, "attachments") is { Length: > 0 } listed && JsonNode.Parse(listed) is JsonArray items)
            {
                foreach (var item in items.OfType<JsonObject>())
                {
                    var kept = new JsonObject
                    {
                        ["name"] = item["name"]?.GetValue<string>() ?? "attachment",
                        ["contentType"] = item["contentType"]?.GetValue<string>() ?? "application/octet-stream",
                        ["size"] = item["size"]?.GetValue<long>() ?? 0,
                    };
                    if (item["skipped"] is { } skipped)
                    {
                        kept["skipped"] = skipped.GetValue<string>();
                    }
                    else if (keep is not null && item["content"]?.GetValue<string>() is { } content)
                    {
                        kept["path"] = keep(uid, kept["name"]!.GetValue<string>(), Convert.FromBase64String(content));
                    }

                    attachments.Add(kept);
                }
            }

            var message = new JsonObject
            {
                ["mailbox"] = mailbox,
                ["uid"] = uid,
                ["messageId"] = Text(record, "message_id"),
                ["date"] = Text(record, "date"),
                ["from"] = Text(record, "from") ?? "",
                ["to"] = Text(record, "to") ?? "",
                ["cc"] = Text(record, "cc") ?? "",
                ["replyTo"] = Text(record, "reply_to") ?? "",
                ["subject"] = Text(record, "subject") ?? "",
                ["text"] = Text(record, "text"),
                ["html"] = Text(record, "html"),
                ["truncated"] = record.Fields.TryGetValue("truncated", out var truncated) && truncated is true,
                ["size"] = Long(record, "size"),
                ["attachments"] = attachments,
            };
            if (record.Fields.TryGetValue("too_large", out var tooLarge) && tooLarge is true)
            {
                message["tooLarge"] = true;
            }

            return message.ToJsonString(Readable);
        }

        // An attachment kept in the site's pipeline folder: mail/{date}/{uid}-{8 hex}-{name}, its name made safe; its path there.
        private string Keep(long uid, string name, byte[] content)
        {
            var root = PipelineFileSandbox.SiteRoot(stepOptions.Value, tenantAccessor.TenantId);
            var relative = "mail/" + clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/" + uid.ToString(CultureInfo.InvariantCulture) + "-"
                + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + "-" + PipelineFormSubmission.SafeName(name);
            var full = PipelineFileSandbox.Resolve(root, relative, out var refusal) ?? throw new InvalidOperationException(refusal);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
            return relative;
        }

        // Through the connection, in the site's scope (the connections, their secrets and the egress are the site's): the records,
        // or null - said in the log - when the site, the connection or its server could not be reached.
        private async Task<IReadOnlyList<ConnectorRecord>?> ExtractAsync(EmailReceivedStimulus stimulus, string operation, Dictionary<string, string> parameters, int cap, CancellationToken ct)
        {
            if (services.GetService(typeof(IConnectorExtractionService)) is not IConnectorExtractionService extraction)
            {
                logger.LogWarning("Mailbox '{Mailbox}' on tenant {Tenant} cannot be looked at: the Connectivity module is not composed in this host.", stimulus.Mailbox, tenantAccessor.TenantId);
                return null;
            }

            if (services.GetService(typeof(ITenantManager)) is not null && !await StommeSiteScope.EnsureSiteAsync(services).ConfigureAwait(false))
            {
                logger.LogWarning("Mailbox '{Mailbox}' cannot be looked at: tenant {Tenant} has no site to enter.", stimulus.Mailbox, tenantAccessor.TenantId);
                return null;
            }

            parameters["mailbox"] = stimulus.Mailbox;
            var result = await extraction.ExtractAsync(Guid.Parse(stimulus.Connection), operation, parameters, cap, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                logger.LogWarning("Mailbox '{Mailbox}' of connection {Connection} on tenant {Tenant} could not be read ({Operation}): {Outcome} - {Message}",
                    stimulus.Mailbox, stimulus.Connection, tenantAccessor.TenantId, operation, result.Outcome, result.Message);
                return null;
            }

            return result.Records;
        }

        private Task SaveAsync(string key, Mark mark, CancellationToken ct) =>
            keyValues.SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = JsonSerializer.Serialize(mark) }, ct);

        private static Mark? Read(string? value)
        {
            try
            {
                return string.IsNullOrEmpty(value) ? null : JsonSerializer.Deserialize<Mark>(value);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        internal static long Long(ConnectorRecord record, string field) =>
            record.Fields.TryGetValue(field, out var value) && value is not null && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

        private static string? Text(ConnectorRecord record, string field) =>
            record.Fields.TryGetValue(field, out var value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

        /// <summary>The stimulus of a stored trigger's payload: typed when the payload serializer kept the type, else read from JSON.</summary>
        internal static EmailReceivedStimulus? ReadStimulus(StoredTrigger trigger) => trigger.Payload switch
        {
            EmailReceivedStimulus stimulus => stimulus,
            JsonElement { ValueKind: JsonValueKind.Object } element => new EmailReceivedStimulus(
                Property(element, nameof(EmailReceivedStimulus.Connection))?.GetString() ?? "",
                Property(element, nameof(EmailReceivedStimulus.Mailbox))?.GetString() ?? PipelineEmailEvent.Inbox,
                Property(element, nameof(EmailReceivedStimulus.MarkRead))?.ValueKind == JsonValueKind.True,
                Property(element, nameof(EmailReceivedStimulus.KeepAttachments))?.ValueKind == JsonValueKind.True),
            IDictionary<string, object> dictionary => new EmailReceivedStimulus(
                Value(dictionary, nameof(EmailReceivedStimulus.Connection)) ?? "",
                Value(dictionary, nameof(EmailReceivedStimulus.Mailbox)) ?? PipelineEmailEvent.Inbox,
                string.Equals(Value(dictionary, nameof(EmailReceivedStimulus.MarkRead)), "true", StringComparison.OrdinalIgnoreCase),
                string.Equals(Value(dictionary, nameof(EmailReceivedStimulus.KeepAttachments)), "true", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };

        private static JsonElement? Property(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }

        private static string? Value(IDictionary<string, object> dictionary, string name) =>
            dictionary.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
    }

    /// <summary>
    /// Looks at the site's watched mailboxes (BL-229): a recurring task the ENGINE runs per site tenant, on one node at a time -
    /// every stored "Email received" trigger's mailbox, once per way of watching it; then a mailbox no trigger watches any more
    /// is forgotten, so publishing it again starts afresh rather than with every message that arrived since.
    /// </summary>
    [SingleNodeTask]
    public sealed class WatchedMailboxesTask(ITriggerStore triggers, WatchedMailboxes mailboxes, ITenantAccessor tenantAccessor, ILogger<WatchedMailboxesTask> logger) : RecurringTask
    {
        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var stored = await triggers.FindManyAsync(new TriggerFilter { Name = PipelineEmailEvent.StepType }, cancellationToken).ConfigureAwait(false);
            var groups = stored.Where(trigger => trigger.Hash is not null).GroupBy(trigger => trigger.Hash!).ToList();
            foreach (var group in groups)
            {
                try
                {
                    if (WatchedMailboxes.ReadStimulus(group.First()) is { } stimulus && EmailReceived.ConnectionProblem(stimulus.Connection) is null)
                    {
                        await mailboxes.LookAsync(group.Key, stimulus, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogWarning("Watched mailbox {Hash} on tenant {Tenant} carries a trigger the task cannot read.", group.Key, tenantAccessor.TenantId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Watched mailbox {Hash} on tenant {Tenant} could not be looked at: {Error}", group.Key, tenantAccessor.TenantId, ex.Message);
                }
            }

            await mailboxes.ForgetAllButAsync(groups.Select(group => group.Key).ToList(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The first look at a mailbox as its pipeline is published (BL-229): when the engine indexes an "Email received" trigger,
    /// where the mailbox is recorded there and then - so a message already there starts nothing, and one arriving a moment after
    /// publishing starts a run though no look has come round yet. Whatever goes wrong is logged and never refuses the publish: the
    /// next look records the mailbox instead.
    /// </summary>
    public sealed class StartWatchingMailboxOnPublish(WatchedMailboxes mailboxes, ILogger<StartWatchingMailboxOnPublish> logger) : INotificationHandler<PipelineTriggersIndexed>
    {
        public async Task HandleAsync(PipelineTriggersIndexed notification, CancellationToken cancellationToken)
        {
            foreach (var trigger in notification.IndexedPipelineTriggers.AddedTriggers.Where(trigger => trigger.Name == PipelineEmailEvent.StepType && trigger.Hash is not null))
            {
                try
                {
                    if (WatchedMailboxes.ReadStimulus(trigger) is { } stimulus && EmailReceived.ConnectionProblem(stimulus.Connection) is null)
                    {
                        await mailboxes.StartWatchingAsync(trigger.Hash!, stimulus, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The mailbox pipeline {Definition} watches could not be recorded as it was published: {Error}", trigger.PipelineDefinitionId, ex.Message);
                }
            }
        }
    }
}
