using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Models;

public record PipelineGraphFindResult(PipelineDefinition? PipelineDefinition, PipelineGraph? PipelineGraph)
{
    public bool PipelineDefinitionExists => PipelineDefinition != null;
    public bool PipelineGraphExists => PipelineGraph != null;
}