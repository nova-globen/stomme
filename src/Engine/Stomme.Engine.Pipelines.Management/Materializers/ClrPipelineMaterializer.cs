using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Management.Materializers;

/// <summary>
/// A pipeline materializer that deserializes pipelines created in C# code.
/// </summary>
public class ClrPipelineMaterializer(
    IPayloadSerializer payloadSerializer,
    IPipelineBuilderFactory pipelineBuilderFactory,
    IServiceProvider serviceProvider) : IPipelineMaterializer
{
    /// <summary>
    /// The name of the materializer.
    /// </summary>
    public const string MaterializerName = "CLR";

    /// <inheritdoc />
    public string Name => MaterializerName;

    /// <inheritdoc />
    public async ValueTask<Pipeline> MaterializeAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        var providerContext = payloadSerializer.Deserialize<ClrPipelineMaterializerContext>(definition.MaterializerContext!);
        var pipelineBuilderType = providerContext.PipelineBuilderType == null! ? typeof(NotFoundPipelineBuilder) : providerContext.PipelineBuilderType;
        var pipelineBuilder = (IPipeline)ActivatorUtilities.GetServiceOrCreateInstance(serviceProvider, pipelineBuilderType);
        var pipelineDefinitionBuilder = pipelineBuilderFactory.CreateBuilder();
        var pipeline = await pipelineDefinitionBuilder.BuildPipelineAsync(pipelineBuilder, cancellationToken).ConfigureAwait(false);

        // Assign identities from the definition.
        pipeline.Identity = new PipelineIdentity(definition.DefinitionId, definition.Version, definition.Id, definition.TenantId);

        return pipeline;
    }
}

/// <summary>
/// Provides context for the CLR pipeline materializer.
/// </summary>
/// <param name="PipelineBuilderType">The type of the pipeline builder.</param>
public record ClrPipelineMaterializerContext(Type PipelineBuilderType);

/// <summary>
/// A pipeline builder that is used when the pipeline builder type is not found.
/// </summary>
public class NotFoundPipelineBuilder : PipelineBase
{
}