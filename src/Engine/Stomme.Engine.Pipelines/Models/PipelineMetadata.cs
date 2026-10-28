namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents metadata about a pipeline.
/// </summary>
/// <param name="Name">The name of the pipeline.</param>
/// <param name="Description">The description of the pipeline.</param>
/// <param name="CreatedAt">The date and time the pipeline was created.</param>
/// <param name="ToolVersion">The version of the tool that created the pipeline.</param>
public record PipelineMetadata(string? Name = default, string? Description = default, DateTimeOffset CreatedAt = default, Version? ToolVersion = default)
{
}