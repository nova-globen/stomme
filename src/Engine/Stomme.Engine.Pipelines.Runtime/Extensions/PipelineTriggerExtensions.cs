using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Runtime.Entities;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Extension methods for <see cref="StoredTrigger"/>.
/// </summary>
public static class PipelineTriggerExtensions
{
    /// <summary>
    /// Filters a collection of <see cref="StoredTrigger"/> by trigger type.
    /// </summary>
    /// <param name="triggers">The collection of triggers to filter.</param>
    /// <typeparam name="T">The trigger type.</typeparam>
    public static IEnumerable<StoredTrigger> Filter<T>(this IEnumerable<StoredTrigger> triggers) where T : ITrigger
    {
        var triggerName = StepTypeNameHelper.GenerateTypeName<T>();
        return triggers.Where(x => x.Name == triggerName);
    }
}