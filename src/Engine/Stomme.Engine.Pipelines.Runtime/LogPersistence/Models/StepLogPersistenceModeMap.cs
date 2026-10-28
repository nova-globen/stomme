using Stomme.Engine.Pipelines.LogPersistence;

namespace Stomme.Engine.Pipelines.Runtime;

public class StepLogPersistenceModeMap
{
    public IDictionary<string, LogPersistenceMode> Inputs { get; set; } = new Dictionary<string, LogPersistenceMode>();
    public IDictionary<string, LogPersistenceMode> Outputs { get; set; } = new Dictionary<string, LogPersistenceMode>();
    public LogPersistenceMode InternalState { get; set; }
}