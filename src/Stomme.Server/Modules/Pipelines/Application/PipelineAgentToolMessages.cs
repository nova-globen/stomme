#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229, the AI agent: the site's published AI agent tools - what an <see cref="AiAgent"/> step can be given - by name, with
    /// what each does and the pipeline it runs; for the editor's Tools input. Read from the trigger store, as the agent reads them.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListAgentToolsQuery : IQuery<IReadOnlyList<AgentToolView>>;

    public sealed class ListAgentToolsQueryHandler(ITriggerStore triggers, IPipelineDefinitionStore definitions) : IQueryHandler<ListAgentToolsQuery, IReadOnlyList<AgentToolView>>
    {
        public async Task<IReadOnlyList<AgentToolView>> Handle(ListAgentToolsQuery request, CancellationToken ct)
        {
            var stored = (await triggers.FindManyAsync(new TriggerFilter { Name = AgentTool.StepTypeName }, ct)).ToList();
            var names = (await definitions.FindSummariesAsync(new PipelineDefinitionFilter { VersionOptions = VersionOptions.Published }, ct))
                .GroupBy(summary => summary.DefinitionId).ToDictionary(group => group.Key, group => group.First().Name);
            return stored
                .Select(trigger => (Tool: AgentTool.Read(trigger), trigger.PipelineDefinitionId))
                .Where(pair => pair.Tool is not null)
                .GroupBy(pair => pair.Tool!.Tool, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(pair => new AgentToolView
                {
                    Name = pair.Tool!.Tool,
                    Description = pair.Tool.Description,
                    DefinitionId = pair.PipelineDefinitionId,
                    PipelineName = names.TryGetValue(pair.PipelineDefinitionId, out var name) ? name : null,
                })
                .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
