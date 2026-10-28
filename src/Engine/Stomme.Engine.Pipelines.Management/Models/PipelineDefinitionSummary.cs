using System.Linq.Expressions;
using Stomme.Engine.Pipelines.Management.Entities;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// A summary of a pipeline definition.
/// </summary>
[PublicAPI]
public class PipelineDefinitionSummary
{
    /// <summary>
    /// Creates a new instance of the <see cref="PipelineDefinitionSummary"/> class from the specified <see cref="PipelineDefinition"/> instance.
    /// </summary>
    public static PipelineDefinitionSummary FromDefinition(PipelineDefinition pipelineDefinition) => new()
    {
        Id = pipelineDefinition.Id,
        DefinitionId = pipelineDefinition.DefinitionId,
        Name = pipelineDefinition.Name,
        Description = pipelineDefinition.Description,
        Version = pipelineDefinition.Version,
        ToolVersion = pipelineDefinition.ToolVersion,
        IsLatest = pipelineDefinition.IsLatest,
        IsPublished = pipelineDefinition.IsPublished,
        ProviderName = pipelineDefinition.ProviderName,
        MaterializerName = pipelineDefinition.MaterializerName,
        CreatedAt = pipelineDefinition.CreatedAt,
        IsReadonly = pipelineDefinition.IsReadonly
    };

    /// <summary>
    /// Creates a new instance of the <see cref="PipelineDefinitionSummary"/> class from the specified <see cref="PipelineDefinition"/> instance.
    /// </summary>
    public static Expression<Func<PipelineDefinition, PipelineDefinitionSummary>> FromDefinitionExpression() => pipelineDefinition => new PipelineDefinitionSummary
    {
        Id = pipelineDefinition.Id,
        DefinitionId = pipelineDefinition.DefinitionId,
        Name = pipelineDefinition.Name,
        Description = pipelineDefinition.Description,
        Version = pipelineDefinition.Version,
        ToolVersion = pipelineDefinition.ToolVersion,
        IsLatest = pipelineDefinition.IsLatest,
        IsPublished = pipelineDefinition.IsPublished,
        ProviderName = pipelineDefinition.ProviderName,
        MaterializerName = pipelineDefinition.MaterializerName,
        CreatedAt = pipelineDefinition.CreatedAt,
        IsReadonly = pipelineDefinition.IsReadonly
    };

    /// <summary>
    /// The version ID of the pipeline definition.
    /// </summary>
    public string Id { get; set; } = null!;

    /// <summary>
    /// The ID of the pipeline definition.
    /// </summary>
    public string DefinitionId { get; set; } = null!;

    /// <summary>
    /// The name of the pipeline definition.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The description of the pipeline definition.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The version of the pipeline definition.
    /// </summary>
    public int? Version { get; set; }

    /// <summary>
    /// The version of the tool that created the pipeline definition.
    /// </summary>
    public Version? ToolVersion { get; set; }

    /// <summary>
    /// Whether this is the latest version of the pipeline definition.
    /// </summary>
    public bool IsLatest { get; set; }

    /// <summary>
    /// Whether this pipeline definition is published.
    /// </summary>
    public bool IsPublished { get; set; }

    /// <summary>
    /// The provider name of the pipeline definition.
    /// </summary>
    public string? ProviderName { get; set; }

    /// <summary>
    /// The materializer name of the pipeline definition.
    /// </summary>
    public string MaterializerName { get; set; } = null!;

    /// <summary>
    /// The timestamp when the pipeline definition was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Makes pipeline
    /// </summary>
    public bool IsReadonly { get; set; }
}