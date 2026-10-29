#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// The site's watched folders (n8n's Local File Trigger): what each "File added" trigger's folder held when it was last
    /// looked at, and the look that starts the pipelines for what is new. Pipelines watching a folder the same way share one
    /// record (one stimulus hash), kept in the engine's key-value store under the SITE's key and the hash - each file's path and
    /// its size and time. A look starts the pipelines indexed under the hash once for each file there now that was not (or, for
    /// "changed files too", whose size or time moved), one run a file, oldest written first. A file written in the last
    /// <c>SettleSeconds</c> waits for the next look, so one still being written is not handed on half-written; at most
    /// <c>MaxRunsPerLook</c> files start runs a look, the rest at the next; only the newest <c>MaxFiles</c> files of a folder are
    /// watched. What the folder holds is recorded BEFORE the runs start, so a file starts its runs once even when a look fails
    /// part way (a run that could not start is logged, not tried again).
    /// </summary>
    public sealed class WatchedFolders(
        IKeyValueStore keyValues,
        IStimulusSender stimulusSender,
        ISystemClock clock,
        ITenantAccessor tenantAccessor,
        IOptions<PipelineTriggerOptions> options,
        IOptions<PipelineStepOptions> stepOptions,
        ILogger<WatchedFolders> logger)
    {
        public const string SeenKeyPrefix = "pipelines:files:watched:";

        /// <summary>Where a site keeps what a watched folder held: its tenant, then the trigger's hash.</summary>
        public static string SeenKey(string? tenantId, string hash) => SeenKeyPrefix + tenantId + ":" + hash;

        /// <summary>
        /// The first look at a folder, when a pipeline watching it is published: what is there is recorded and starts nothing (n8n's
        /// "ignore existing files", its default). A folder already watched the same way keeps its record. False when the folder
        /// cannot be looked at, said in the log.
        /// </summary>
        public async Task<bool> StartWatchingAsync(string hash, FileAddedStimulus stimulus, CancellationToken ct)
        {
            if (Folder(stimulus) is not { } place)
            {
                return false;
            }

            var (root, folder) = place;

            var key = SeenKey(tenantAccessor.TenantId, hash);
            if (await keyValues.FindAsync(new KeyValueFilter { Key = key }, ct).ConfigureAwait(false) is null)
            {
                var files = List(root, folder, stimulus, Settings.MaxFiles, out _);
                await SaveAsync(key, files.ToDictionary(file => file.Path, file => file.Signature, StringComparer.Ordinal), ct).ConfigureAwait(false);
                logger.LogInformation("Folder '{Folder}' on tenant {Tenant} is watched from now on; the {Count} file(s) there start nothing.", stimulus.Folder, tenantAccessor.TenantId, files.Count);
            }

            return true;
        }

        /// <summary>A look at a watched folder: the pipelines started for each file new since the last; the first look only records.</summary>
        public async Task LookAsync(string hash, FileAddedStimulus stimulus, CancellationToken ct)
        {
            var tenant = tenantAccessor.TenantId;
            var key = SeenKey(tenant, hash);
            var last = await keyValues.FindAsync(new KeyValueFilter { Key = key }, ct).ConfigureAwait(false);
            if (last is null)
            {
                // Its publish did not record it (it was published before the folder was watched, or the record was forgotten).
                await StartWatchingAsync(hash, stimulus, ct).ConfigureAwait(false);
                return;
            }

            if (Folder(stimulus) is not { } place)
            {
                return;
            }

            var (root, folder) = place;

            var settings = Settings;
            var files = List(root, folder, stimulus, settings.MaxFiles, out var held);
            if (held > files.Count)
            {
                logger.LogWarning("Watched folder '{Folder}' on tenant {Tenant} holds {Held} files; the newest {Watched} are watched.", stimulus.Folder, tenant, held, files.Count);
            }

            var seen = Read(last.SerializedValue);
            var next = new Dictionary<string, string>(StringComparer.Ordinal);
            var due = new List<(WatchedFile File, string Change)>();
            var settled = clock.UtcNow.AddSeconds(-Math.Max(0, settings.SettleSeconds));
            foreach (var file in files)
            {
                var known = seen.TryGetValue(file.Path, out var was);
                var change = !known ? PipelineFileEvent.Added : stimulus.Changed && was != file.Signature ? PipelineFileEvent.Changed : null;
                if (change is not null && (file.ModifiedUtc > settled || due.Count >= Math.Max(1, settings.MaxRunsPerLook)))
                {
                    // Still being written, or past this look's runs: the next look sees it as this one did.
                    if (known)
                    {
                        next[file.Path] = was!;
                    }

                    continue;
                }

                next[file.Path] = file.Signature;
                if (change is not null)
                {
                    due.Add((file, change));
                }
            }

            if (next.Count != seen.Count || next.Any(pair => !seen.TryGetValue(pair.Key, out var was) || was != pair.Value))
            {
                await SaveAsync(key, next, ct).ConfigureAwait(false);
            }

            foreach (var (file, change) in due)
            {
                try
                {
                    var result = await stimulusSender.SendAsync(hash, new StimulusMetadata
                    {
                        Input = new Dictionary<string, object> { [FileAdded.FileInputKey] = PipelineFileEvent.Write(file.Path, file.Size, file.ModifiedUtc, change) },
                    }, ct).ConfigureAwait(false);
                    logger.LogInformation("File '{Path}' ({Change}) on tenant {Tenant} started {Count} pipeline(s).", file.Path, change, tenant, result.PipelineInstanceResponses.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "File '{Path}' on tenant {Tenant} could not start its pipelines: {Error}", file.Path, tenant, ex.Message);
                }
            }
        }

        /// <summary>Forgets every folder of the site no trigger watches any more, so publishing one again starts afresh.</summary>
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

        private FileTriggerOptions Settings => options.Value.FileTriggers;

        // The site's folder and the watched folder in it - or null, said in the log, when it cannot be looked at.
        private (string Root, string Folder)? Folder(FileAddedStimulus stimulus)
        {
            string root;
            try
            {
                root = PipelineFileSandbox.SiteRoot(stepOptions.Value, tenantAccessor.TenantId);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("Watched folder '{Folder}' on tenant {Tenant} cannot be looked at: {Error}", stimulus.Folder, tenantAccessor.TenantId, ex.Message);
                return null;
            }

            string? refusal = null;
            var folder = stimulus.Folder.Length == 0 ? root : PipelineFileSandbox.Resolve(root, stimulus.Folder, out refusal);
            if (folder is null)
            {
                logger.LogWarning("Watched folder '{Folder}' on tenant {Tenant} cannot be looked at: {Error}", stimulus.Folder, tenantAccessor.TenantId, refusal);
                return null;
            }

            return (root, folder);
        }

        /// <summary>A file as a look sees it: its path in the site's folder, its size and time, and the two as one signature.</summary>
        internal sealed record WatchedFile(string Path, long Size, DateTimeOffset ModifiedUtc)
        {
            public string Signature => Size + ":" + ModifiedUtc.UtcTicks;
        }

        // The folder's files the trigger takes, oldest written first - at most the newest 'most'; how many it holds in all. Hidden
        // and system files and links are passed over: a link could lead out of the site's folder.
        internal static List<WatchedFile> List(string root, string folder, FileAddedStimulus stimulus, int most, out int held)
        {
            held = 0;
            if (!Directory.Exists(folder))
            {
                return new List<WatchedFile>(); // a folder not made yet holds nothing; a file put there once it is made is new
            }

            var patterns = PipelineFileEvent.Patterns(stimulus.Pattern);
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = stimulus.Subfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };
            var found = new DirectoryInfo(folder).EnumerateFiles("*", enumeration)
                .Where(file => PipelineFileEvent.Matches(file.Name, patterns))
                .Select(file => new WatchedFile(System.IO.Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)))
                .ToList();
            held = found.Count;
            return found.OrderByDescending(file => file.ModifiedUtc).ThenBy(file => file.Path, StringComparer.Ordinal).Take(Math.Max(1, most))
                .OrderBy(file => file.ModifiedUtc).ThenBy(file => file.Path, StringComparer.Ordinal).ToList();
        }

        private Task SaveAsync(string key, Dictionary<string, string> files, CancellationToken ct) =>
            keyValues.SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = JsonSerializer.Serialize(files) }, ct);

        private static Dictionary<string, string> Read(string? value)
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(value ?? "") is { } files ? new Dictionary<string, string>(files, StringComparer.Ordinal) : new(StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        /// <summary>The stimulus of a stored trigger's payload: typed when the payload serializer kept the type, else read from JSON.</summary>
        internal static FileAddedStimulus? ReadStimulus(StoredTrigger trigger) => trigger.Payload switch
        {
            FileAddedStimulus stimulus => stimulus,
            JsonElement { ValueKind: JsonValueKind.Object } element => new FileAddedStimulus(
                Property(element, nameof(FileAddedStimulus.Folder))?.GetString() ?? "",
                Property(element, nameof(FileAddedStimulus.Pattern))?.GetString() ?? "",
                Property(element, nameof(FileAddedStimulus.Subfolders))?.ValueKind == JsonValueKind.True,
                Property(element, nameof(FileAddedStimulus.Changed))?.ValueKind == JsonValueKind.True),
            IDictionary<string, object> dictionary => new FileAddedStimulus(
                Value(dictionary, nameof(FileAddedStimulus.Folder)) ?? "",
                Value(dictionary, nameof(FileAddedStimulus.Pattern)) ?? "",
                string.Equals(Value(dictionary, nameof(FileAddedStimulus.Subfolders)), "true", StringComparison.OrdinalIgnoreCase),
                string.Equals(Value(dictionary, nameof(FileAddedStimulus.Changed)), "true", StringComparison.OrdinalIgnoreCase)),
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
    /// Looks at the site's watched folders: a recurring task the ENGINE runs per site tenant (so the trigger store, the
    /// key-value store and the stimulus sender are the site's), on one node at a time - every stored "File added" trigger's
    /// folder, once per way of watching it; then a folder no trigger watches any more is forgotten (its pipeline retracted,
    /// archived or changed to watch another), so publishing it again starts afresh rather than with every file put there since.
    /// </summary>
    [SingleNodeTask]
    public sealed class WatchedFoldersTask(ITriggerStore triggers, WatchedFolders folders, ITenantAccessor tenantAccessor, ILogger<WatchedFoldersTask> logger) : RecurringTask
    {
        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var stored = await triggers.FindManyAsync(new TriggerFilter { Name = PipelineFileEvent.StepType }, cancellationToken).ConfigureAwait(false);
            var groups = stored.Where(trigger => trigger.Hash is not null).GroupBy(trigger => trigger.Hash!).ToList();
            foreach (var group in groups)
            {
                try
                {
                    if (WatchedFolders.ReadStimulus(group.First()) is { } stimulus)
                    {
                        await folders.LookAsync(group.Key, stimulus, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogWarning("Watched folder {Hash} on tenant {Tenant} carries a trigger the task cannot read.", group.Key, tenantAccessor.TenantId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Watched folder {Hash} on tenant {Tenant} could not be looked at: {Error}", group.Key, tenantAccessor.TenantId, ex.Message);
                }
            }

            await folders.ForgetAllButAsync(groups.Select(group => group.Key).ToList(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The first look at a folder as its pipeline is published: when the engine indexes a "File added" trigger, what the
    /// folder holds is recorded there and then - so "a file already there when the pipeline is published starts nothing" means
    /// exactly that, and a file put there a moment after publishing starts a run though no look has come round yet. Whatever
    /// goes wrong is logged and never refuses the publish: the next look records the folder instead.
    /// </summary>
    public sealed class StartWatchingOnPublish(WatchedFolders folders, ILogger<StartWatchingOnPublish> logger) : INotificationHandler<PipelineTriggersIndexed>
    {
        public async Task HandleAsync(PipelineTriggersIndexed notification, CancellationToken cancellationToken)
        {
            foreach (var trigger in notification.IndexedPipelineTriggers.AddedTriggers.Where(trigger => trigger.Name == PipelineFileEvent.StepType && trigger.Hash is not null))
            {
                try
                {
                    if (WatchedFolders.ReadStimulus(trigger) is { } stimulus)
                    {
                        await folders.StartWatchingAsync(trigger.Hash!, stimulus, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The folder pipeline {Definition} watches could not be recorded as it was published: {Error}", trigger.PipelineDefinitionId, ex.Message);
                }
            }
        }
    }
}
