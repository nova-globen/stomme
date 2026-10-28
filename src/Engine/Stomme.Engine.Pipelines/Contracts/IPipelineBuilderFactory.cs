namespace Stomme.Engine.Pipelines;

/// <summary>
/// A factory of pipeline builders.
/// </summary>
public interface IPipelineBuilderFactory
{
    /// <summary>
    /// Creates a new pipeline builder.
    /// </summary>
    /// <returns>A new pipeline builder.</returns>
    IPipelineBuilder CreateBuilder();
}