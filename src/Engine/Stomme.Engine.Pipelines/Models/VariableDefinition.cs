namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Stores information about a pipeline variable.
/// </summary>
public record VariableDefinition(string Id, string Name, string TypeName, bool IsArray, string? Value, string? StorageDriverTypeName);