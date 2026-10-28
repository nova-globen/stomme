using System.Linq.Expressions;
using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents a summary view of a <see cref="PipelineInstance"/>.
/// </summary>
public class PipelineInstanceSummary
{
    /// <summary>
    /// Returns a summary view of the specified <see cref="PipelineInstance"/>.
    /// </summary>
    public static PipelineInstanceSummary FromInstance(PipelineInstance pipelineInstance)
    {
        return new()
        {
            Id = pipelineInstance.Id,
            TenantId = pipelineInstance.TenantId,
            DefinitionId = pipelineInstance.DefinitionId,
            DefinitionVersionId = pipelineInstance.DefinitionVersionId,
            Version = pipelineInstance.Version,
            Status = pipelineInstance.Status,
            SubStatus = pipelineInstance.SubStatus,
            CorrelationId = pipelineInstance.CorrelationId,
            Name = pipelineInstance.Name,
            IncidentCount = pipelineInstance.IncidentCount,
            CreatedAt = pipelineInstance.CreatedAt,
            UpdatedAt = pipelineInstance.UpdatedAt,
            FinishedAt = pipelineInstance.FinishedAt
        };
    }

    /// <summary>
    /// Returns a summary view of the specified <see cref="PipelineInstance"/>.
    /// </summary>
    public static Expression<Func<PipelineInstance, PipelineInstanceSummary>> FromInstanceExpression() => pipelineInstance => new()
    {
        Id = pipelineInstance.Id,
        TenantId = pipelineInstance.TenantId,
        DefinitionId = pipelineInstance.DefinitionId,
        DefinitionVersionId = pipelineInstance.DefinitionVersionId,
        Version = pipelineInstance.Version,
        Status = pipelineInstance.Status,
        SubStatus = pipelineInstance.SubStatus,
        CorrelationId = pipelineInstance.CorrelationId,
        Name = pipelineInstance.Name,
        IncidentCount = pipelineInstance.IncidentCount,
        CreatedAt = pipelineInstance.CreatedAt,
        UpdatedAt = pipelineInstance.UpdatedAt,
        FinishedAt = pipelineInstance.FinishedAt
    };


    /// <summary>The ID of the pipeline instance.</summary>
    public string Id { get; set; } = null!;

    /// <summary>The ID of the tenant that owns the pipeline instance.</summary>
    public string? TenantId { get; set; }

    /// <summary>The ID of the pipeline definition.</summary>
    public string DefinitionId { get; set; } = null!;

    /// <summary>The version ID of the pipeline definition.</summary>
    public string DefinitionVersionId { get; set; } = null!;

    /// <summary>The version of the pipeline definition.</summary>
    public int Version { get; set; }

    /// <summary>The status of the pipeline instance.</summary>
    public PipelineStatus Status { get; set; }

    /// <summary>The sub-status of the pipeline instance.</summary>
    public PipelineSubStatus SubStatus { get; set; }

    /// <summary>The ID of the pipeline instance.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>The name of the pipeline instance.</summary>
    public string? Name { get; set; }

    /// <summary>The number of incidents associated with the pipeline instance.</summary>
    public int IncidentCount { get; set; }

    /// <summary>The timestamp when the pipeline instance was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The timestamp when the pipeline instance was last executed.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The timestamp when the pipeline instance was finished.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}