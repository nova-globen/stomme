namespace Stomme.Engine.Pipelines.LogPersistence;

public interface ILogPersistenceStrategy
{
    Task<LogPersistenceMode> GetPersistenceModeAsync(LogPersistenceStrategyContext context);
}