using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.LogPersistence;

[UsedImplicitly]
public class DefaultLogPersistenceStrategyService(IEnumerable<ILogPersistenceStrategy> strategies) : ILogPersistenceStrategyService
{
    public IEnumerable<ILogPersistenceStrategy> ListStrategies()
    {
        return strategies;
    }
}