namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents the result of exporting one or more pipeline definitions.
/// </summary>
/// <param name="Data">The binary content (JSON or ZIP).</param>
/// <param name="FileName">The suggested file name.</param>
public record PipelineDefinitionExportResult(byte[] Data, string FileName);