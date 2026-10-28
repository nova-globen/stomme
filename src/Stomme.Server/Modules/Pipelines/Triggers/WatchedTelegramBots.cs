#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
    /// The site's watched Telegram bots (BL-229, n8n's Telegram Trigger): for each bot a "Telegram message received" trigger
    /// watches, the last update handed on - and the look that starts the pipelines for each message since. Every pipeline on a bot
    /// shares one record (one stimulus hash: the connection alone), kept in the engine's key-value store under the SITE's key and
    /// the hash. A look asks the bot, through the connection (<see cref="IConnectorExtractionService"/>: the connector, the token
    /// from the site's secret store, the host's egress - the pipeline never holds it), for the updates after the last, at most
    /// <c>MaxMessagesPerLook</c>; records the highest update id BEFORE starting runs, so a message starts its runs once even when a
    /// look fails part way; and starts the pipelines indexed under the hash, one run a message or channel post - any other kind
    /// of update (an edit, a button pressed) is moved past. Asking with the next offset is how Telegram is told the earlier ones
    /// were handed on: it forgets them.
    /// </summary>
    public sealed class WatchedTelegramBots(
        IKeyValueStore keyValues,
        IStimulusSender stimulusSender,
        ITenantAccessor tenantAccessor,
        IServiceProvider services,
        IOptions<PipelineTriggerOptions> options,
        ILogger<WatchedTelegramBots> logger)
    {
        public const string SeenKeyPrefix = "pipelines:telegram:watched:";

        /// <summary>Where a site keeps how far a watched bot was read: its tenant, then the trigger's hash.</summary>
        public static string SeenKey(string? tenantId, string hash) => SeenKeyPrefix + tenantId + ":" + hash;

        /// <summary>How far a bot was read: the last update id handed on.</summary>
        public sealed record Mark(long LastUpdateId);

        /// <summary>
        /// The first look at a bot, when a pipeline watching it is published: the newest update waiting is recorded - Telegram
        /// forgetting the ones before it - so the messages waiting then start nothing and the next one starts a run. A bot already
        /// watched keeps its record. False when the bot could not be asked - said in the log, and the next look records it instead.
        /// </summary>
        public async Task<bool> StartWatchingAsync(string hash, TelegramMessageStimulus stimulus, CancellationToken ct)
        {
            var key = SeenKey(tenantAccessor.TenantId, hash);
            if (await keyValues.FindAsync(new KeyValueFilter { Key = key }, ct).ConfigureAwait(false) is not null)
            {
                return true;
            }

            var latest = await ExtractAsync(stimulus, "latest", new Dictionary<string, string>(), 1, ct).ConfigureAwait(false);
            if (latest?.FirstOrDefault() is not { } record || Refused(stimulus, record))
            {
                return false;
            }

            var mark = new Mark(WatchedMailboxes.Long(record, "update_id"));
            await SaveAsync(key, mark, ct).ConfigureAwait(false);
            logger.LogInformation("Telegram bot of connection {Connection} on tenant {Tenant} is watched from update {Update} on; the messages waiting start nothing.",
                stimulus.Connection, tenantAccessor.TenantId, mark.LastUpdateId);
            return true;
        }

        /// <summary>A look at a watched bot: the pipelines started for each message since the last look; the first look only records.</summary>
        public async Task LookAsync(string hash, TelegramMessageStimulus stimulus, CancellationToken ct)
        {
            var tenant = tenantAccessor.TenantId;
            var key = SeenKey(tenant, hash);
            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = key }, ct).ConfigureAwait(false);
            if (Read(stored?.SerializedValue) is not { } mark)
            {
                // Its publish did not record it (the bot could not be asked then, or the record was forgotten).
                await StartWatchingAsync(hash, stimulus, ct).ConfigureAwait(false);
                return;
            }

            var most = Math.Clamp(options.Value.TelegramTriggers.MaxMessagesPerLook, 1, 100);
            var records = await ExtractAsync(stimulus, "updates", new Dictionary<string, string>
            {
                ["offset"] = (mark.LastUpdateId + 1).ToString(CultureInfo.InvariantCulture),
                ["limit"] = most.ToString(CultureInfo.InvariantCulture),
            }, most, ct).ConfigureAwait(false);
            if (records is null || records.Count == 0 || Refused(stimulus, records[0]))
            {
                return;
            }

            var updates = records.Where(record => WatchedMailboxes.Long(record, "update_id") > mark.LastUpdateId).OrderBy(record => WatchedMailboxes.Long(record, "update_id")).ToList();
            if (updates.Count == 0)
            {
                return;
            }

            // Recorded before any run starts: a message starts its runs once, even when this look fails part way.
            await SaveAsync(key, mark with { LastUpdateId = WatchedMailboxes.Long(updates[^1], "update_id") }, ct).ConfigureAwait(false);
            foreach (var update in updates)
            {
                var id = WatchedMailboxes.Long(update, "update_id");
                if (Message(update) is not { } message)
                {
                    logger.LogDebug("Update {Update} of the Telegram bot of connection {Connection} on tenant {Tenant} is no message; moved past.", id, stimulus.Connection, tenant);
                    continue;
                }

                try
                {
                    var result = await stimulusSender.SendAsync(hash, new StimulusMetadata
                    {
                        Input = new Dictionary<string, object> { [TelegramMessageReceived.MessageInputKey] = PipelineTelegramEvent.Write(message) },
                    }, ct).ConfigureAwait(false);
                    logger.LogInformation("Telegram update {Update} on tenant {Tenant} started {Count} pipeline(s).", id, tenant, result.PipelineInstanceResponses.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Telegram update {Update} on tenant {Tenant} could not start its pipelines: {Error}", id, tenant, ex.Message);
                }
            }
        }

        /// <summary>Forgets every bot of the site no trigger watches any more, so publishing one again starts afresh.</summary>
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

        /// <summary>A message or a channel post read from the connector's record; null for any other kind of update.</summary>
        internal static PipelineTelegramMessage? Message(ConnectorRecord record)
        {
            var kind = Text(record, "kind");
            if (kind is not (PipelineTelegramEvent.MessageKind or PipelineTelegramEvent.ChannelPostKind) || WatchedMailboxes.Long(record, "message_id") <= 0)
            {
                return null;
            }

            return new PipelineTelegramMessage(
                WatchedMailboxes.Long(record, "update_id"),
                kind,
                WatchedMailboxes.Long(record, "message_id"),
                DateTimeOffset.FromUnixTimeSeconds(WatchedMailboxes.Long(record, "date")),
                Text(record, "text") ?? "",
                WatchedMailboxes.Long(record, "chat_id"),
                Text(record, "chat_type") ?? "",
                Text(record, "chat_title"),
                Text(record, "chat_username"),
                record.Fields.TryGetValue("from_id", out var from) && from is not null ? WatchedMailboxes.Long(record, "from_id") : null,
                Text(record, "from_username"),
                Text(record, "from_first_name"),
                Text(record, "from_last_name"),
                record.Fields.TryGetValue("reply_to_message_id", out var reply) && reply is not null ? WatchedMailboxes.Long(record, "reply_to_message_id") : null);
        }

        // Telegram's refusal - a webhook set on the bot, a token it does not know - said in the log; nothing recorded or started.
        private bool Refused(TelegramMessageStimulus stimulus, ConnectorRecord record)
        {
            if (Text(record, "refused") is not { } reason)
            {
                return false;
            }

            logger.LogWarning("The Telegram bot of connection {Connection} on tenant {Tenant} could not be asked for its messages - {Reason}. A bot with a webhook set (n8n's trigger sets one) answers no one else until it is deleted.",
                stimulus.Connection, tenantAccessor.TenantId, reason);
            return true;
        }

        // Through the connection, in the site's scope (the connections, their secrets and the egress are the site's): the records,
        // or null - said in the log - when the site, the connection or Telegram could not be reached.
        private async Task<IReadOnlyList<ConnectorRecord>?> ExtractAsync(TelegramMessageStimulus stimulus, string operation, Dictionary<string, string> parameters, int cap, CancellationToken ct)
        {
            if (services.GetService(typeof(IConnectorExtractionService)) is not IConnectorExtractionService extraction)
            {
                logger.LogWarning("The Telegram bot of connection {Connection} on tenant {Tenant} cannot be asked: the Connectivity module is not composed in this host.", stimulus.Connection, tenantAccessor.TenantId);
                return null;
            }

            if (services.GetService(typeof(ITenantManager)) is not null && !await StommeSiteScope.EnsureSiteAsync(services).ConfigureAwait(false))
            {
                logger.LogWarning("The Telegram bot of connection {Connection} cannot be asked: tenant {Tenant} has no site to enter.", stimulus.Connection, tenantAccessor.TenantId);
                return null;
            }

            var result = await extraction.ExtractAsync(Guid.Parse(stimulus.Connection), operation, parameters, cap, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                logger.LogWarning("The Telegram bot of connection {Connection} on tenant {Tenant} could not be asked ({Operation}): {Outcome} - {Message}",
                    stimulus.Connection, tenantAccessor.TenantId, operation, result.Outcome, result.Message);
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

        private static string? Text(ConnectorRecord record, string field) =>
            record.Fields.TryGetValue(field, out var value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

        /// <summary>The stimulus of a stored trigger's payload: typed when the payload serializer kept the type, else read from JSON.</summary>
        internal static TelegramMessageStimulus? ReadStimulus(StoredTrigger trigger) => trigger.Payload switch
        {
            TelegramMessageStimulus stimulus => stimulus,
            JsonElement { ValueKind: JsonValueKind.Object } element => element.EnumerateObject()
                .Where(property => string.Equals(property.Name, nameof(TelegramMessageStimulus.Connection), StringComparison.OrdinalIgnoreCase))
                .Select(property => new TelegramMessageStimulus(property.Value.GetString() ?? ""))
                .FirstOrDefault(),
            IDictionary<string, object> dictionary => dictionary
                .Where(pair => string.Equals(pair.Key, nameof(TelegramMessageStimulus.Connection), StringComparison.OrdinalIgnoreCase))
                .Select(pair => new TelegramMessageStimulus(pair.Value?.ToString() ?? ""))
                .FirstOrDefault(),
            _ => null,
        };
    }

    /// <summary>
    /// Asks the site's watched Telegram bots for their messages (BL-229): a recurring task the ENGINE runs per site tenant, on one
    /// node at a time - every bot a stored "Telegram message received" trigger watches, once; then a bot no trigger watches any
    /// more is forgotten, so publishing it again starts afresh rather than with every message that came since.
    /// </summary>
    [SingleNodeTask]
    public sealed class WatchedTelegramBotsTask(ITriggerStore triggers, WatchedTelegramBots bots, ITenantAccessor tenantAccessor, ILogger<WatchedTelegramBotsTask> logger) : RecurringTask
    {
        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var stored = await triggers.FindManyAsync(new TriggerFilter { Name = PipelineTelegramEvent.StepType }, cancellationToken).ConfigureAwait(false);
            var groups = stored.Where(trigger => trigger.Hash is not null).GroupBy(trigger => trigger.Hash!).ToList();
            foreach (var group in groups)
            {
                try
                {
                    if (WatchedTelegramBots.ReadStimulus(group.First()) is { } stimulus && TelegramMessageReceived.ConnectionProblem(stimulus.Connection) is null)
                    {
                        await bots.LookAsync(group.Key, stimulus, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogWarning("Watched Telegram bot {Hash} on tenant {Tenant} carries a trigger the task cannot read.", group.Key, tenantAccessor.TenantId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Watched Telegram bot {Hash} on tenant {Tenant} could not be asked: {Error}", group.Key, tenantAccessor.TenantId, ex.Message);
                }
            }

            await bots.ForgetAllButAsync(groups.Select(group => group.Key).ToList(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The first look at a bot as its pipeline is published (BL-229): when the engine indexes a "Telegram message received"
    /// trigger, the newest update is recorded there and then - so a message waiting starts nothing, and one coming a moment after
    /// publishing starts a run though no look has come round yet. Whatever goes wrong is logged and never refuses the publish: the
    /// next look records the bot instead.
    /// </summary>
    public sealed class StartWatchingTelegramBotOnPublish(WatchedTelegramBots bots, ILogger<StartWatchingTelegramBotOnPublish> logger) : INotificationHandler<PipelineTriggersIndexed>
    {
        public async Task HandleAsync(PipelineTriggersIndexed notification, CancellationToken cancellationToken)
        {
            foreach (var trigger in notification.IndexedPipelineTriggers.AddedTriggers.Where(trigger => trigger.Name == PipelineTelegramEvent.StepType && trigger.Hash is not null))
            {
                try
                {
                    if (WatchedTelegramBots.ReadStimulus(trigger) is { } stimulus && TelegramMessageReceived.ConnectionProblem(stimulus.Connection) is null)
                    {
                        await bots.StartWatchingAsync(trigger.Hash!, stimulus, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The Telegram bot pipeline {Definition} watches could not be recorded as it was published: {Error}", trigger.PipelineDefinitionId, ex.Message);
                }
            }
        }
    }
}
