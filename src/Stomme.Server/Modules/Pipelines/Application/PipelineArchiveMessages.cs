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
    /// n8n's archive: the pipelines set aside, kept beside their versions in the engine's key-value store as their tags are
    /// (archiving is not an edit of a version, and never conflicts with a collaborator's save). The key carries the SITE, as the
    /// tags' does (<see cref="PipelineTagStore"/>).
    /// </summary>
    public static class PipelineArchiveStore
    {
        public const string KeyPrefix = "pipelines:archived:";

        public static string Prefix(string? tenantId) => KeyPrefix + tenantId + ":";

        public static string Key(string? tenantId, string definitionId) => Prefix(tenantId) + definitionId;

        /// <summary>When a pipeline was archived, and by whom.</summary>
        public sealed record Archived(DateTime OnUtc, string? By);

        /// <summary>Every archived pipeline of the site, by definition id.</summary>
        public static async Task<IReadOnlyDictionary<string, Archived>> ReadAllAsync(IKeyValueStore keyValues, string? tenantId, CancellationToken ct)
        {
            var prefix = Prefix(tenantId);
            var stored = await keyValues.FindManyAsync(new KeyValueFilter { Key = prefix, StartsWith = true }, ct);
            var archived = new Dictionary<string, Archived>(StringComparer.Ordinal);
            foreach (var pair in stored.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                archived[pair.Key[prefix.Length..]] = Read(pair.SerializedValue);
            }

            return archived;
        }

        public static async Task<Archived?> FindAsync(IKeyValueStore keyValues, string? tenantId, string definitionId, CancellationToken ct) =>
            await keyValues.FindAsync(new KeyValueFilter { Key = Key(tenantId, definitionId) }, ct) is { } pair ? Read(pair.SerializedValue) : null;

        public static Task SaveAsync(IKeyValueStore keyValues, string? tenantId, string definitionId, Archived archived, CancellationToken ct) =>
            keyValues.SaveAsync(new SerializedKeyValuePair { Key = Key(tenantId, definitionId), SerializedValue = JsonSerializer.Serialize(archived) }, ct);

        public static Task DeleteAsync(IKeyValueStore keyValues, string? tenantId, string definitionId, CancellationToken ct) =>
            keyValues.DeleteAsync(Key(tenantId, definitionId), ct);

        /// <summary>The archive's say on a view: when and by whom, or nothing.</summary>
        public static void Apply(PipelineView view, Archived? archived)
        {
            view.ArchivedOnUtc = archived?.OnUtc;
            view.ArchivedBy = archived?.By;
        }

        // Stored by this class only; anything unreadable is still archived - set aside is the safe reading - with no time known.
        private static Archived Read(string? json)
        {
            try
            {
                return JsonSerializer.Deserialize<Archived>(json ?? "") ?? new Archived(DateTime.MinValue, null);
            }
            catch (JsonException)
            {
                return new Archived(DateTime.MinValue, null);
            }
        }
    }

    /// <summary>
    /// n8n's archive: a pipeline set aside - unpublished first, so no trigger starts it and nothing runs it, then marked, so
    /// the list keeps it out of the way until it is restored or deleted. Its versions, runs and tags stay. False when the site has
    /// no such pipeline.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record ArchivePipelineCommand(string DefinitionId) : ICommand<bool>;

    public sealed class ArchivePipelineCommandHandler(IPipelineDefinitionStore definitions, IPipelineDefinitionPublisher publisher, IKeyValueStore keyValues,
        ITenantAccessor tenants, ICurrentUser currentUser, Stomme.Engine.Common.ISystemClock clock) : ICommandHandler<ArchivePipelineCommand, bool>
    {
        public async Task<bool> Handle(ArchivePipelineCommand request, CancellationToken ct)
        {
            if (await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct) is null)
            {
                return false;
            }

            if (await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Published }, ct) is not null)
            {
                await publisher.RetractAsync(request.DefinitionId, ct);
            }

            var who = !string.IsNullOrWhiteSpace(currentUser.UserName) ? currentUser.UserName : currentUser.UserId is { } id ? "user " + id : null;
            await PipelineArchiveStore.SaveAsync(keyValues, tenants.TenantId, request.DefinitionId, new PipelineArchiveStore.Archived(clock.UtcNow.UtcDateTime, who), ct);
            return true;
        }
    }

    /// <summary>
    /// A pipeline back from the archive, as a draft: it is published again by its author, as n8n's restored workflow is
    /// activated again. False when the site has no such pipeline.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record RestorePipelineCommand(string DefinitionId) : ICommand<bool>;

    public sealed class RestorePipelineCommandHandler(IPipelineDefinitionStore definitions, IKeyValueStore keyValues, ITenantAccessor tenants)
        : ICommandHandler<RestorePipelineCommand, bool>
    {
        public async Task<bool> Handle(RestorePipelineCommand request, CancellationToken ct)
        {
            if (await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct) is null)
            {
                return false;
            }

            await PipelineArchiveStore.DeleteAsync(keyValues, tenants.TenantId, request.DefinitionId, ct);
            return true;
        }
    }
}
