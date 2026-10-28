using Stomme.Engine.Common;
using Stomme.Engine.Common.RecurringTasks;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Tasks;

/// <summary>
/// Updates the pipeline store from <see cref="IPipelinesProvider"/> implementations, creates triggers and updates the <see cref="IStepRegistry"/>.
/// </summary>
[UsedImplicitly]
[SingleNodeTask]
public class PopulateRegistriesStartupTask(IRegistriesPopulator registriesPopulator) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await registriesPopulator.PopulateAsync(cancellationToken).ConfigureAwait(false);
    }
}