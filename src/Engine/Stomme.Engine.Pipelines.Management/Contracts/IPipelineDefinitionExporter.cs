using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Exports pipeline definitions as serialized JSON or ZIP archives.
/// </summary>
public interface IPipelineDefinitionExporter
{
    /// <summary>
    /// Exports a single pipeline definition as a JSON byte array, optionally including consuming pipelines as a ZIP archive.
    /// </summary>
    /// <param name="definitionId">The definition ID.</param>
    /// <param name="versionOptions">The version options. Defaults to <see cref="VersionOptions.Latest"/>.</param>
    /// <param name="includeConsumingPipelines">When true, includes all consuming pipeline definitions in the export as a ZIP archive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An export result containing the binary content and a suggested file name, or null if the definition was not found.</returns>
    Task<PipelineDefinitionExportResult?> ExportAsync(string definitionId, VersionOptions? versionOptions = null, bool includeConsumingPipelines = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports multiple pipeline definitions as a ZIP archive.
    /// </summary>
    /// <param name="ids">A list of pipeline definition version IDs.</param>
    /// <param name="includeConsumingPipelines">When true, includes all consuming pipeline definitions in the export.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An export result containing the ZIP binary content and a suggested file name, or null if no definitions were found.</returns>
    Task<PipelineDefinitionExportResult?> ExportManyAsync(ICollection<string> ids, bool includeConsumingPipelines = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes a single pipeline definition entity to a JSON byte array (with $schema header).
    /// </summary>
    /// <param name="definition">The pipeline definition entity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The serialized JSON bytes and suggested file name.</returns>
    Task<PipelineDefinitionExportResult> ExportDefinitionAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports multiple pipeline definition entities as a ZIP archive.
    /// </summary>
    /// <param name="definitions">The pipeline definitions to export.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ZIP archive bytes and suggested file name.</returns>
    Task<PipelineDefinitionExportResult> ExportDefinitionsAsync(ICollection<PipelineDefinition> definitions, CancellationToken cancellationToken = default);
}