namespace Stomme.Engine.Pipelines.LogPersistence;

public interface ILogPersistenceStrategyService
{
    IEnumerable<ILogPersistenceStrategy> ListStrategies();
}