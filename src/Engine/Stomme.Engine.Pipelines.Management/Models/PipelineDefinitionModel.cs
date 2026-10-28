using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents a serializable pipeline definition.
/// </summary>
[PublicAPI]
public class PipelineDefinitionModel
{
    /// <summary>
    /// Represents a serializable pipeline definition.
    /// </summary>
    public PipelineDefinitionModel() { }

    /// <summary>
    /// Represents a serializable pipeline definition.
    /// </summary>
    public PipelineDefinitionModel(string id,
        string definitionId,
        string? tenantId,
        string? name,
        string? description,
        DateTimeOffset createdAt,
        int version,
        Version? toolVersion,
        ICollection<VariableDefinition>? variables,
        ICollection<InputDefinition>? inputs,
        ICollection<OutputDefinition>? outputs,
        ICollection<string>? outcomes,
        IDictionary<string, object>? customProperties,
        bool isReadonly,
        bool isSystem,
        bool isLatest,
        bool isPublished,
        PipelineOptions? options,
        bool? usableAsStep,
        IStep? root)
    {
        Id = id;
        DefinitionId = definitionId;
        TenantId = tenantId;
        Name = name;
        Description = description;
        CreatedAt = createdAt;
        Version = version;
        ToolVersion = toolVersion;
        Variables = variables;
        Inputs = inputs;
        Outputs = outputs;
        Outcomes = outcomes;
        CustomProperties = customProperties;
        IsReadonly = isReadonly;
        IsSystem = isSystem;
        IsLatest = isLatest;
        IsPublished = isPublished;
        Options = options;
        UsableAsStep = usableAsStep;
        Root = root;
    }

    public string Id { get; set; } = null!;
    public string DefinitionId { get; set; } = null!;
    public string? TenantId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Version { get; set; }
    public Version? ToolVersion { get; set; }
    public ICollection<VariableDefinition>? Variables { get; set; }
    public ICollection<InputDefinition>? Inputs { get; set; }
    public ICollection<OutputDefinition>? Outputs { get; set; }
    public ICollection<string>? Outcomes { get; set; }
    public IDictionary<string, object>? CustomProperties { get; set; }
    public bool IsReadonly { get; set; }
    public bool IsSystem { get; set; }
    public bool IsLatest { get; set; }
    public bool IsPublished { get; set; }
    public PipelineOptions? Options { get; set; }

    [Obsolete("Use Options.UsableAsStep instead")]
    public bool? UsableAsStep { get; set; }

    public IStep? Root { get; set; }
}