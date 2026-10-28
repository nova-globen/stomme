using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Management.Contracts;

/// <summary>
/// Creates new <see cref="PipelineInstance"/> objects.
/// </summary>
public interface IPipelineInstanceFactory
{
    /// <summary>
    /// Creates a new <see cref="PipelineState"/> object.
    /// </summary>
    PipelineState CreatePipelineState(Pipeline pipeline, PipelineInstanceOptions? options = null);

    /// <summary>
    /// Creates a new <see cref="PipelineInstance"/> object.
    /// </summary>
    PipelineInstance CreatePipelineInstance(Pipeline pipeline, PipelineInstanceOptions? options = null);
}