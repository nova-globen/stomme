using System.IO.Compression;
using System.Text.Json;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Mappers;
using Stomme.Engine.Pipelines.Management.Models;
using Humanizer;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class PipelineDefinitionExporter(
    IPipelineDefinitionStore store,
    IApiSerializer serializer,
    PipelineDefinitionMapper pipelineDefinitionMapper,
    IPipelineReferenceGraphBuilder pipelineReferenceGraphBuilder,
    IFileNameSanitizer fileNameSanitizer) : IPipelineDefinitionExporter
{
    /// <inheritdoc />
    public async Task<PipelineDefinitionExportResult?> ExportAsync(string definitionId, VersionOptions? versionOptions = null, bool includeConsumingPipelines = false, CancellationToken cancellationToken = default)
    {
        var parsedVersionOptions = versionOptions ?? VersionOptions.Latest;
        var definition = (await store.FindManyAsync(new()
        {
            DefinitionId = definitionId,
            VersionOptions = parsedVersionOptions
        }, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

        if (definition == null)
            return null;

        if (includeConsumingPipelines)
        {
            var definitions = await IncludeConsumersAsync([definition], cancellationToken).ConfigureAwait(false);
            return await ExportDefinitionsAsync(definitions, cancellationToken).ConfigureAwait(false);
        }

        return await ExportDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinitionExportResult?> ExportManyAsync(ICollection<string> ids, bool includeConsumingPipelines = false, CancellationToken cancellationToken = default)
    {
        var definitions = (await store.FindManyAsync(new()
        {
            Ids = ids
        }, cancellationToken).ConfigureAwait(false)).ToList();

        if (includeConsumingPipelines)
            definitions = await IncludeConsumersAsync(definitions, cancellationToken).ConfigureAwait(false);

        if (definitions.Count == 0)
            return null;

        return await ExportDefinitionsAsync(definitions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinitionExportResult> ExportDefinitionAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        var model = await pipelineDefinitionMapper.MapAsync(definition, cancellationToken).ConfigureAwait(false);
        var binaryJson = await SerializePipelineDefinitionAsync(model, cancellationToken).ConfigureAwait(false);
        var fileName = GetFileName(model);
        return new(binaryJson, fileName);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinitionExportResult> ExportDefinitionsAsync(ICollection<PipelineDefinition> definitions, CancellationToken cancellationToken = default)
    {
        var zipBytes = await CreateZipArchiveAsync(definitions, cancellationToken).ConfigureAwait(false);
        return new(zipBytes, "pipeline-definitions.zip");
    }

    /// <summary>
    /// Recursively discovers all consuming pipeline definitions and includes them.
    /// Consumers are always resolved at <see cref="VersionOptions.Latest"/>, regardless of the version used for the initial definitions.
    /// </summary>
    private async Task<List<PipelineDefinition>> IncludeConsumersAsync(List<PipelineDefinition> definitions, CancellationToken cancellationToken)
    {
        var initialDefinitionIds = definitions.Select(d => d.DefinitionId).ToList();
        var graph = await pipelineReferenceGraphBuilder.BuildGraphAsync(initialDefinitionIds, cancellationToken).ConfigureAwait(false);

        // Find any consumer definitions not already in our list.
        var newDefinitionIds = graph.ConsumerDefinitionIds.Except(initialDefinitionIds).ToList();

        if (newDefinitionIds.Count > 0)
        {
            var consumerDefinitions = await store.FindManyAsync(new()
            {
                DefinitionIds = newDefinitionIds.ToArray(),
                VersionOptions = VersionOptions.Latest
            }, cancellationToken).ConfigureAwait(false);

            definitions = definitions.Concat(consumerDefinitions).ToList();
        }

        return definitions;
    }

    private async Task<byte[]> CreateZipArchiveAsync(ICollection<PipelineDefinition> definitions, CancellationToken cancellationToken)
    {
        var zipStream = new MemoryStream();
        var sortedDefinitions = definitions.OrderBy(d => d.DefinitionId).ToList();

        // NOTE:
        // - ZIP timestamps cannot be earlier than 1980-01-01 (the ZIP format's minimum).
        // - We intentionally use a fixed timestamp (instead of DateTimeOffset.UtcNow) to keep exports deterministic.
        //   This avoids producing different ZIP bytes for identical exports, which helps tests, caching, and diffing.
        var zipEpoch = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

#if NET10_0_OR_GREATER
        var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Create, true);
        await using (zipArchive.ConfigureAwait(false))
        {
            foreach (var definition in sortedDefinitions)
            {
                var model = await pipelineDefinitionMapper.MapAsync(definition, cancellationToken).ConfigureAwait(false);
                var binaryJson = await SerializePipelineDefinitionAsync(model, cancellationToken).ConfigureAwait(false);
                var fileName = GetFileName(model);
                var entry = zipArchive.CreateEntry(fileName, CompressionLevel.Optimal);
                entry.LastWriteTime = zipEpoch;
                var entryStream = (await entry.OpenAsync(cancellationToken).ConfigureAwait(false));
                await using var _entryStreamAsyncDisposal = entryStream.ConfigureAwait(false);
                await entryStream.WriteAsync(binaryJson, cancellationToken).ConfigureAwait(false);
            }
        }
#else
        using (var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
        {
            foreach (var definition in sortedDefinitions)
            {
                var model = await pipelineDefinitionMapper.MapAsync(definition, cancellationToken);
                var binaryJson = await SerializePipelineDefinitionAsync(model, cancellationToken);
                var fileName = GetFileName(model);
                var entry = zipArchive.CreateEntry(fileName, CompressionLevel.Optimal);
                entry.LastWriteTime = zipEpoch;
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(binaryJson, cancellationToken);
            }
        }
#endif

        zipStream.Position = 0;
        return zipStream.ToArray();
    }

    private string GetFileName(PipelineDefinitionModel definition)
    {
        var hasPipelineName = !string.IsNullOrWhiteSpace(definition.Name);
        var pipelineName = hasPipelineName ? definition.Name!.Trim() : definition.DefinitionId;
        var pipelineSlug = pipelineName.Underscore().Dasherize().ToLowerInvariant();
        var dynamicFileNamePart = $"{pipelineSlug}-{definition.DefinitionId}-{definition.Id}";
        var sanitizedDynamicFileNamePart = fileNameSanitizer.Sanitize(dynamicFileNamePart);

        return $"pipeline-definition-{sanitizedDynamicFileNamePart}.json";
    }

    private async Task<byte[]> SerializePipelineDefinitionAsync(PipelineDefinitionModel model, CancellationToken cancellationToken)
    {
        var serializerOptions = serializer.GetOptions();
        using var document = JsonSerializer.SerializeToDocument(model, serializerOptions);
        var rootElement = document.RootElement;

        using var output = new MemoryStream();
        var writer = new Utf8JsonWriter(output);
        await using var _writerAsyncDisposal = writer.ConfigureAwait(false);

        writer.WriteStartObject();
        writer.WriteString("$schema", "https://enginepipelines.io/schemas/pipeline-definition/v3.0.0/schema.json");

        foreach (var property in rootElement.EnumerateObject())
            property.WriteTo(writer);

        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }
}
