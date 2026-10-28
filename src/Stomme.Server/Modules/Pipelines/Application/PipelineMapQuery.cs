#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229 - the map of the site's pipelines (<see cref="PipelineLinks"/>): each pipeline's latest version, and its published one
    /// when that is another - the one that runs - so a link either holds is drawn; the archived left out, as the list leaves them
    /// out. Takes what viewing pipelines takes.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetPipelineMapQuery : IQuery<PipelineMapView>;

    public sealed class GetPipelineMapQueryHandler(IPipelineDefinitionStore definitions, IKeyValueStore keyValues, ITenantAccessor tenants)
        : IQueryHandler<GetPipelineMapQuery, PipelineMapView>
    {
        public async Task<PipelineMapView> Handle(GetPipelineMapQuery request, CancellationToken ct)
        {
            var archived = await PipelineArchiveStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            var folders = await PipelineFolderStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            var latest = await definitions.FindManyAsync(new PipelineDefinitionFilter { VersionOptions = VersionOptions.Latest }, ct);
            var published = (await definitions.FindManyAsync(new PipelineDefinitionFilter { VersionOptions = VersionOptions.Published }, ct))
                .GroupBy(definition => definition.DefinitionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var pipelines = new List<(string, string?, string?, bool, PipelineGraph?)>();
            foreach (var definition in latest.Where(definition => !archived.ContainsKey(definition.DefinitionId)))
            {
                var graph = Read(definition.StringData);
                if (published.TryGetValue(definition.DefinitionId, out var running) && running.Version != definition.Version && Read(running.StringData) is { } ran)
                {
                    graph = graph is null ? ran : new PipelineGraph { Steps = graph.Steps.Concat(ran.Steps).ToList() };
                }

                pipelines.Add((definition.DefinitionId, definition.Name, folders.GetValueOrDefault(definition.DefinitionId), published.ContainsKey(definition.DefinitionId), graph));
            }

            return PipelineLinks.Map(pipelines);
        }

        private static PipelineGraph? Read(string? body)
        {
            try
            {
                return PipelineGraphCodec.ToGraph(body);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
            {
                return null; // a body the editor did not draw: on the map, linking nothing
            }
        }
    }
}
