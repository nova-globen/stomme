namespace Stomme.Engine.Pipelines.Exceptions;

/// <summary>
/// Thrown when a NotFoundStep is executed.
/// </summary>
public class StepNotFoundException : Exception
{
    /// <inheritdoc />
    public StepNotFoundException(string missingTypeName) : base($"Step type '{missingTypeName}' could not be found.")
    {
        MissingTypeName = missingTypeName;
    }
    
    /// <inheritdoc />
    public StepNotFoundException(string missingTypeName, int missingTypeVersion) : base($"Step type '{missingTypeName}' version '{missingTypeVersion}' could not be found.")
    {
        MissingTypeName = missingTypeName;
        MissingTypeVersion = missingTypeVersion;
    }
    
    /// <summary>
    /// The type name of the missing step type.
    /// </summary>
    public string MissingTypeName { get; }
    
    /// <summary>
    /// The version of the missing step type.
    /// </summary>
    public int MissingTypeVersion { get; }
}