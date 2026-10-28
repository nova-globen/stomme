using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;

namespace Stomme.Engine.Pipelines.Runtime.Extensions;

/// <summary>
/// Provides extension methods for the <see cref="ITriggerStore"/> interface.
/// </summary>
public static class StoredTriggerStoreExtensions
{
    /// <summary>
    /// Finds all triggers that match the specified stimulus.
    /// </summary>
    public static ValueTask<IEnumerable<StoredTrigger>> FindManyByStimulusHashAsync(this ITriggerStore triggerStore, string stimulusHash, CancellationToken cancellationToken = default)
    {
        var filter = new TriggerFilter
        {
            Hash = stimulusHash
        };
        return triggerStore.FindManyAsync(filter, cancellationToken);
    }
}