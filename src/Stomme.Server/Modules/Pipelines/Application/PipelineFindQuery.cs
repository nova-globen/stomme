#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229 - find in every pipeline (<see cref="PipelineContentSearch"/>): where a text is used in the latest version of each of
    /// the site's pipelines - archived ones left out, as the list leaves them out - pipeline by pipeline in name order, at most
    /// <see cref="PipelineContentSearch.MaxResults"/> places. A sensitive input is never read. Takes what viewing pipelines takes.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record FindInPipelinesQuery(string? Text) : IQuery<IReadOnlyList<PipelineFoundView>>;

    public sealed class FindInPipelinesQueryHandler(IPipelineDefinitionStore definitions, IStepRegistry registry, IRegistriesPopulator populator, IKeyValueStore keyValues, ITenantAccessor tenants)
        : IQueryHandler<FindInPipelinesQuery, IReadOnlyList<PipelineFoundView>>
    {
        public async Task<IReadOnlyList<PipelineFoundView>> Handle(FindInPipelinesQuery request, CancellationToken ct)
        {
            var found = new List<PipelineFoundView>();
            if ((request.Text?.Trim().Length ?? 0) < PipelineContentSearch.MinLength)
            {
                return found;
            }

            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            var catalog = ListStepsQueryHandler.Catalog(registry);
            var archived = await PipelineArchiveStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            var folders = await PipelineFolderStore.ReadAllAsync(keyValues, tenants.TenantId, ct);
            var latest = await definitions.FindManyAsync(new PipelineDefinitionFilter { VersionOptions = VersionOptions.Latest }, ct);
            foreach (var definition in latest.Where(definition => !archived.ContainsKey(definition.DefinitionId)).OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                PipelineGraph graph;
                try
                {
                    graph = PipelineGraphCodec.ToGraph(definition.StringData);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    continue; // a body the editor did not draw has no graph to search
                }

                foreach (var (stepId, where, snippet) in PipelineContentSearch.Find(graph, request.Text, catalog))
                {
                    found.Add(new PipelineFoundView
                    {
                        DefinitionId = definition.DefinitionId,
                        PipelineName = definition.Name,
                        Folder = folders.GetValueOrDefault(definition.DefinitionId) ?? "",
                        StepId = stepId,
                        Where = where,
                        Snippet = snippet,
                    });
                    if (found.Count >= PipelineContentSearch.MaxResults)
                    {
                        return found;
                    }
                }
            }

            return found;
        }
    }
}
