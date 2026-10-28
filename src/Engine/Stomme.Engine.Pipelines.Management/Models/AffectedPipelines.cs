using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management.Models;

public record AffectedPipelines(ICollection<PipelineDefinition> PipelineDefinitions);