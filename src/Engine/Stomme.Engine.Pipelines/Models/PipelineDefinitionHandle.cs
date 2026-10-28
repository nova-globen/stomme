using Stomme.Engine.Common.Models;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents a handle to a pipeline definition.
/// </summary>
public class PipelineDefinitionHandle
{
    /// <summary>
    /// Gets or sets the definition ID. When set, the <see cref="DefinitionVersionId"/> property is ignored.
    /// </summary>
    public string? DefinitionId { get; set; }
    
    /// <summary>
    /// Gets or sets the version options. When set, the <see cref="DefinitionVersionId"/> property is ignored.
    /// </summary>
    public VersionOptions? VersionOptions { get; set; }

    /// <summary>
    /// Gets or sets the definition version ID. When set, the <see cref="DefinitionId"/> and <see cref="VersionOptions"/> properties are ignored.
    /// </summary>
    public string? DefinitionVersionId { get; set; }
    
    /// <summary>
    /// Creates a new <see cref="PipelineDefinitionHandle"/> instance with the specified definition ID and version options.
    /// </summary>
    public static PipelineDefinitionHandle ByDefinitionId(string definitionId, VersionOptions? versionOptions = null) => new() { DefinitionId = definitionId, VersionOptions = versionOptions };
    
    /// <summary>
    /// Creates a new <see cref="PipelineDefinitionHandle"/> instance with the specified definition version ID.
    /// </summary>
    /// <param name="definitionVersionId"></param>
    /// <returns></returns>
    public static PipelineDefinitionHandle ByDefinitionVersionId(string definitionVersionId) => new() { DefinitionVersionId = definitionVersionId };

    /// <inheritdoc />
    public override string ToString()
    {
        if (DefinitionId != null)
            return $"DefinitionId: {DefinitionId}, VersionOptions: {VersionOptions}";
        
        if (DefinitionVersionId != null)
            return $"DefinitionVersionId: {DefinitionVersionId}";

        return string.Empty;
    }
}