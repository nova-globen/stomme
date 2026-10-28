#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// BL-229 - n8n's folders: the folder a pipeline sits in on the pipelines list, kept as its tags are - beside its versions in
    /// the engine's key-value store, under the site (<see cref="PipelineTagStore"/>): moving a pipeline is not an edit, publishes
    /// nothing and never conflicts with a collaborator's save.
    /// </summary>
    public static class PipelineFolderStore
    {
        public const string KeyPrefix = "pipelines:folder:";

        public static string Prefix(string? tenantId) => KeyPrefix + tenantId + ":";

        public static string Key(string? tenantId, string definitionId) => Prefix(tenantId) + definitionId;

        /// <summary>Every pipeline of the site in a folder: its definition id and its folder.</summary>
        public static async Task<IReadOnlyDictionary<string, string>> ReadAllAsync(IKeyValueStore keyValues, string? tenantId, CancellationToken ct)
        {
            var prefix = Prefix(tenantId);
            var stored = await keyValues.FindManyAsync(new KeyValueFilter { Key = prefix, StartsWith = true }, ct);
            var folders = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in stored.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                // Stored by this class only; anything unreadable is the top rather than a broken list.
                var folder = PipelineFolders.Normalize(pair.SerializedValue, out var error);
                if (error is null && folder.Length > 0)
                {
                    folders[pair.Key[prefix.Length..]] = folder;
                }
            }

            return folders;
        }

        /// <summary>The folder one pipeline sits in; empty at the top of the list.</summary>
        public static async Task<string> FindAsync(IKeyValueStore keyValues, string? tenantId, string definitionId, CancellationToken ct)
        {
            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = Key(tenantId, definitionId) }, ct);
            var folder = PipelineFolders.Normalize(stored?.SerializedValue, out var error);
            return error is null ? folder : "";
        }

        public static async Task SaveAsync(IKeyValueStore keyValues, string? tenantId, string definitionId, string folder, CancellationToken ct)
        {
            var key = Key(tenantId, definitionId);
            if (string.IsNullOrEmpty(folder))
            {
                await keyValues.DeleteAsync(key, ct);
                return;
            }

            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = folder }, ct);
        }
    }

    /// <summary>Moves a pipeline to a folder (empty: the top of the list); null when the site has no such pipeline. A folder that
    /// cannot be one is refused with the reason (<see cref="PipelineFolders"/>).</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record SetPipelineFolderCommand(string DefinitionId, string? Folder) : ICommand<string?>;

    public sealed class SetPipelineFolderCommandHandler(IPipelineDefinitionStore definitions, IKeyValueStore keyValues, ITenantAccessor tenants)
        : ICommandHandler<SetPipelineFolderCommand, string?>
    {
        public async Task<string?> Handle(SetPipelineFolderCommand request, CancellationToken ct)
        {
            var folder = PipelineFolders.Normalize(request.Folder, out var error);
            if (error is not null)
            {
                // No parameter name: it would append " (Parameter 'Folder')" to the reason the author reads.
                throw new ArgumentException(error);
            }

            var definition = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct);
            if (definition is null)
            {
                return null;
            }

            // Spelled as the site's other pipelines spell it, level by level: one folder, whatever case it is asked in.
            var others = await PipelineFolderStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            folder = PipelineFolders.Spelled(folder, others.Where(pair => pair.Key != request.DefinitionId).Select(pair => pair.Value));
            await PipelineFolderStore.SaveAsync(keyValues, tenants.TenantId, request.DefinitionId, folder, ct);
            return folder;
        }
    }

    /// <summary>
    /// BL-229 - n8n's folder rename, move and delete (<see cref="PipelineFolderMoveRequest"/>): every pipeline in the folder, or
    /// in a folder inside it, goes along to the new path - or, removed, up to the folder above, where a folder of the same name
    /// is the same folder. Null when no pipeline is in the folder; a move that cannot be is refused with the reason
    /// (<see cref="PipelineFolders.MoveProblem"/>).
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record MovePipelineFolderCommand(string Folder, string? NewFolder, bool Remove) : ICommand<PipelineFolderMoveResult?>;

    public sealed class MovePipelineFolderCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants)
        : ICommandHandler<MovePipelineFolderCommand, PipelineFolderMoveResult?>
    {
        public async Task<PipelineFolderMoveResult?> Handle(MovePipelineFolderCommand request, CancellationToken ct)
        {
            var stored = await PipelineFolderStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            var from = PipelineFolders.Normalize(request.Folder, out _);
            var inIt = stored.Where(pair => from.Length > 0 && PipelineFolders.IsWithin(pair.Value, from)).ToList();
            if (inIt.Count == 0)
            {
                return null;
            }

            // The folder as the list spells it: the one asked for, whatever its case.
            from = PipelineFolders.All(stored.Values).First(folder => PipelineFolders.IsIn(folder, from));
            var others = stored.Where(pair => !PipelineFolders.IsWithin(pair.Value, from)).Select(pair => pair.Value).ToList();
            string to;
            if (request.Remove)
            {
                to = PipelineFolders.Parent(from);
            }
            else
            {
                to = PipelineFolders.Normalize(request.NewFolder, out var error);
                if ((error ?? PipelineFolders.MoveProblem(stored.Values, from, to)) is { } problem)
                {
                    // No parameter name: it would append " (Parameter ...)" to the reason the author reads.
                    throw new ArgumentException(problem);
                }

                // Placed among the folders it is not in as they spell them: "a/B" into "A" is "A/B".
                to = PipelineFolders.Spelled(to, others);
            }

            foreach (var (definitionId, folder) in inIt)
            {
                // Removed, what was in it joins the folders above as they are spelled; renamed, the new path is the spelling.
                var moved = PipelineFolders.Moved(folder, from, to);
                await PipelineFolderStore.SaveAsync(keyValues, tenants.TenantId, definitionId, request.Remove ? PipelineFolders.Spelled(moved, others) : moved, ct);
            }

            return new PipelineFolderMoveResult { Folder = to, Moved = inIt.Count };
        }
    }
}
