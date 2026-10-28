using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Management.Materializers;
using Stomme.Engine.Pipelines.Runtime.Features;
using Stomme.Engine.Pipelines.Runtime.Options;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime.Providers;

/// <summary>
/// Provides pipelines to the system that are registered with <see cref="PipelineRuntimeFeature"/>
/// </summary>
[UsedImplicitly]
public class ClrPipelinesProvider(
    IOptions<RuntimeOptions> options,
    IPipelineBuilderFactory pipelineBuilderFactory,
    IServiceProvider serviceProvider) : IPipelinesProvider
{
    /// <inheritdoc />
    public string Name => "CLR";

    /// <inheritdoc />
    public async ValueTask<IEnumerable<MaterializedPipeline>> GetPipelinesAsync(CancellationToken cancellationToken = default)
    {
        var buildPipelineTasks = options.Value.Pipelines.Values.Select(async x => await BuildPipelineAsync(x, cancellationToken).ConfigureAwait(false)).ToList();
        var pipelineDefinitions = await Task.WhenAll(buildPipelineTasks).ConfigureAwait(false);
        return pipelineDefinitions;
    }

    private async Task<MaterializedPipeline> BuildPipelineAsync(Func<IServiceProvider, ValueTask<IPipeline>> pipelineFactory, CancellationToken cancellationToken)
    {
        var builder = pipelineBuilderFactory.CreateBuilder();
        var pipelineBuilder = await pipelineFactory(serviceProvider).ConfigureAwait(false);
        var pipelineBuilderType = pipelineBuilder.GetType();
        await pipelineBuilder.BuildAsync(builder, cancellationToken).ConfigureAwait(false);
        var pipeline = await builder.BuildPipelineAsync(cancellationToken).ConfigureAwait(false);
        var versionSuffix = $"v{pipeline.Version}";
        var definitionId = string.IsNullOrEmpty(pipeline.Identity.DefinitionId) ? pipelineBuilderType.Name : pipeline.Identity.DefinitionId;
        var id = string.IsNullOrEmpty(pipeline.Identity.Id) ? $"{pipelineBuilderType.Name}:{versionSuffix}" : pipeline.Identity.Id;
        
        pipeline.Identity = pipeline.Identity with
        {
            Id = id,
            DefinitionId = definitionId,
            TenantId = pipeline.Identity.TenantId ?? Tenant.AgnosticTenantId
        };

        var materializerContext = new ClrPipelineMaterializerContext(pipelineBuilder.GetType());
        return new(pipeline, Name, ClrPipelineMaterializer.MaterializerName, materializerContext);
    }
}