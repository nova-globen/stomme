using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management;

public static class PipelineDefinitionHandleExtensions
{
    public static PipelineDefinitionFilter ToFilter(this PipelineDefinitionHandle handle)
    {
        return new()
        {
            DefinitionId = handle.DefinitionId,
            Id = handle.DefinitionVersionId,
            VersionOptions = handle.VersionOptions
        };
    }
}