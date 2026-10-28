#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229 - n8n's workflow tags: a pipeline's tags, kept beside its versions in the engine's key-value store (a tag is the
    /// pipeline's, not a version's: tagging is not an edit, publishes nothing and never conflicts with a collaborator's save).
    /// The key carries the SITE: a key-value key is one row per installation while the store reads per site (the schedules
    /// task's lesson, <see cref="Triggers.ScheduledPipelinesTask.LastFiredKey"/>).
    /// </summary>
    public static class PipelineTagStore
    {
        public const string KeyPrefix = "pipelines:tags:";

        public static string Prefix(string? tenantId) => KeyPrefix + tenantId + ":";

        public static string Key(string? tenantId, string definitionId) => Prefix(tenantId) + definitionId;

        /// <summary>Every tagged pipeline of the site: its definition id and its tags.</summary>
        public static async Task<IReadOnlyDictionary<string, List<string>>> ReadAllAsync(IKeyValueStore keyValues, string? tenantId, CancellationToken ct)
        {
            var prefix = Prefix(tenantId);
            var stored = await keyValues.FindManyAsync(new KeyValueFilter { Key = prefix, StartsWith = true }, ct);
            var tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pair in stored.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                tags[pair.Key[prefix.Length..]] = Read(pair.SerializedValue);
            }

            return tags;
        }

        public static async Task SaveAsync(IKeyValueStore keyValues, string? tenantId, string definitionId, IReadOnlyCollection<string> tags, CancellationToken ct)
        {
            var key = Key(tenantId, definitionId);
            if (tags.Count == 0)
            {
                await keyValues.DeleteAsync(key, ct);
                return;
            }

            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = JsonSerializer.Serialize(tags) }, ct);
        }

        // Stored by this class only; anything unreadable is no tags rather than a broken list.
        private static List<string> Read(string? json)
        {
            try
            {
                return PipelineTags.Normalize(JsonSerializer.Deserialize<List<string?>>(json ?? "[]"), out _);
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }
    }

    /// <summary>Replaces a pipeline's tags (an empty list clears them); null when the site has no such pipeline. A tag that
    /// cannot be one is refused with the reason (<see cref="PipelineTags"/>).</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record SetPipelineTagsCommand(string DefinitionId, IReadOnlyList<string>? Tags) : ICommand<IReadOnlyList<string>?>;

    public sealed class SetPipelineTagsCommandHandler(IPipelineDefinitionStore definitions, IKeyValueStore keyValues, ITenantAccessor tenants)
        : ICommandHandler<SetPipelineTagsCommand, IReadOnlyList<string>?>
    {
        public async Task<IReadOnlyList<string>?> Handle(SetPipelineTagsCommand request, CancellationToken ct)
        {
            var tags = PipelineTags.Normalize(request.Tags, out var error);
            if (error is not null)
            {
                // No parameter name: it would append " (Parameter 'Tags')" to the reason the author reads.
                throw new ArgumentException(error);
            }

            var definition = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct);
            if (definition is null)
            {
                return null;
            }

            await PipelineTagStore.SaveAsync(keyValues, tenants.TenantId, request.DefinitionId, tags, ct);
            return tags;
        }
    }
}
