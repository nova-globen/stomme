using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.LogPersistence.Strategies;

/// <summary>
/// A log persistence strategy that returns <see cref="LogPersistenceMode.Inherit"/>.
/// </summary>
[UsedImplicitly]
public class Inherit : ILogPersistenceStrategy
{
    public Task<LogPersistenceMode> GetPersistenceModeAsync(LogPersistenceStrategyContext context)
    {
        return Task.FromResult(LogPersistenceMode.Inherit);
    }
}