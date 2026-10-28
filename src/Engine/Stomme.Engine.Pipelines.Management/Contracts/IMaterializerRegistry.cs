namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Provides access to registered pipeline materializers and their availability.
/// </summary>
public interface IMaterializerRegistry
{
    /// <summary>
    /// Gets all registered materializers.
    /// </summary>
    IEnumerable<IPipelineMaterializer> GetMaterializers();

    /// <summary>
    /// Gets a materializer by name.
    /// </summary>
    /// <param name="name">The name of the materializer.</param>
    /// <returns>The materializer, or null if not found.</returns>
    IPipelineMaterializer? GetMaterializer(string name);

    /// <summary>
    /// Checks if a materializer with the specified name is available.
    /// </summary>
    /// <param name="name">The name of the materializer.</param>
    /// <returns>True if the materializer is available, false otherwise.</returns>
    bool IsMaterializerAvailable(string name);
}
