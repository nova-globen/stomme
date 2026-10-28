using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Management.Mappers;

/// <summary>
/// Maps a pipeline state to a pipeline instance.
/// </summary>
public class PipelineStateMapper
{
    /// <summary>
    /// [Obsolete] The property key name used to store the pipeline instance name.
    /// </summary>
    [Obsolete("This constant is obsolete and retained only for backward compatibility. Avoid using it in new code.")]
    private const string PipelineInstanceNameKey = "PipelineInstanceName";
    
    /// <summary>
    /// Maps a pipeline state to a pipeline instance.
    /// </summary>
    public PipelineInstance? Map(PipelineState? source)
    {
        if (source == null)
            return null;

        var pipelineInstance = new PipelineInstance();
        Apply(source, pipelineInstance);

        return pipelineInstance;
    }

    /// <summary>
    /// Maps a pipeline state to a pipeline instance.
    /// </summary>
    public void Apply(PipelineState source, PipelineInstance target)
    {
        target.Id = source.Id;
        target.CreatedAt = source.CreatedAt;
        target.DefinitionId = source.DefinitionId;
        target.DefinitionVersionId = source.DefinitionVersionId;
        target.Version = source.DefinitionVersion;
        target.ParentPipelineInstanceId = source.ParentPipelineInstanceId;
        target.Status = source.Status;
        target.SubStatus = source.SubStatus;
        target.IsExecuting = source.IsExecuting;
        target.CorrelationId = source.CorrelationId;
        target.Name = source.Name;
        target.IncidentCount = source.Incidents.Count;
        target.IsSystem = source.IsSystem;
        target.UpdatedAt = source.UpdatedAt;
        target.FinishedAt = source.FinishedAt;
        target.PipelineState = source;
        
        // Keep for backward compatibility with pipeline instances created before the introduction of the Name property.
        if (source.Properties.TryGetValue<string>(PipelineInstanceNameKey, out var name))
            target.Name = name;
    }

    /// <summary>
    /// Maps a pipeline instance to a pipeline state.
    /// </summary>
    public PipelineState? Map(PipelineInstance? source)
    {
        if (source == null)
            return null;

        var pipelineState = source.PipelineState;
        pipelineState.Id = source.Id;
        pipelineState.CreatedAt = source.CreatedAt;
        pipelineState.DefinitionId = source.DefinitionId;
        pipelineState.DefinitionVersionId = source.DefinitionVersionId;
        pipelineState.DefinitionVersion = source.Version;
        pipelineState.ParentPipelineInstanceId = source.ParentPipelineInstanceId;
        pipelineState.Status = source.Status;
        pipelineState.SubStatus = source.SubStatus;
        pipelineState.IsExecuting = source.IsExecuting;
        pipelineState.CorrelationId = source.CorrelationId;
        pipelineState.Name = source.Name;
        pipelineState.UpdatedAt = source.UpdatedAt;
        pipelineState.FinishedAt = source.FinishedAt;
        pipelineState.IsSystem = source.IsSystem;

        return pipelineState;
    }
}