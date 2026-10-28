namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class PipelineDefinitionsReloader(IRegistriesPopulator registriesPopulator) : IPipelineDefinitionsReloader
{
    /// <inheritdoc />
    public async Task ReloadPipelineDefinitionsAsync(CancellationToken cancellationToken)
    {
        await registriesPopulator.PopulateAsync(cancellationToken).ConfigureAwait(false);
    }
}