using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class PipelineInstanceFactory(IIdentityGenerator identityGenerator, ISystemClock systemClock) : IPipelineInstanceFactory
{
    /// <inheritdoc />
    public PipelineState CreatePipelineState(Pipeline pipeline, PipelineInstanceOptions? options = null)
    {
        var now = systemClock.UtcNow;
        return new()
        {
            Id = string.IsNullOrEmpty(options?.PipelineInstanceId) ? identityGenerator.GenerateId() : options.PipelineInstanceId,
            DefinitionId = pipeline.Identity.DefinitionId,
            DefinitionVersionId = pipeline.Identity.Id,
            DefinitionVersion = pipeline.Identity.Version,
            CorrelationId = options?.CorrelationId,
            Name = options?.Name,
            Input = options?.Input ?? new Dictionary<string, object>(),
            Properties = options?.Properties ?? new Dictionary<string, object>(),
            Status = PipelineStatus.Running,
            SubStatus = PipelineSubStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            ParentPipelineInstanceId = options?.ParentPipelineInstanceId,
            IsSystem = pipeline.IsSystem
        };
    }

    /// <inheritdoc />
    public PipelineInstance CreatePipelineInstance(Pipeline pipeline, PipelineInstanceOptions? options = null)
    {
        var pipelineState = CreatePipelineState(pipeline, options);
        return new()
        {
            Id = pipelineState.Id,
            ParentPipelineInstanceId = pipelineState.ParentPipelineInstanceId,
            PipelineState = pipelineState,
            DefinitionId = pipelineState.DefinitionId,
            DefinitionVersionId = pipelineState.DefinitionVersionId,
            Version = pipelineState.DefinitionVersion,
            CorrelationId = pipelineState.CorrelationId,
            Name = pipelineState.Name,
            Status = pipelineState.Status,
            SubStatus = pipelineState.SubStatus,
            IncidentCount = pipelineState.Incidents.Count,
            IsSystem = pipelineState.IsSystem,
            CreatedAt = pipelineState.CreatedAt,
            UpdatedAt = pipelineState.UpdatedAt,
            FinishedAt = pipelineState.FinishedAt,
        };
    }
}