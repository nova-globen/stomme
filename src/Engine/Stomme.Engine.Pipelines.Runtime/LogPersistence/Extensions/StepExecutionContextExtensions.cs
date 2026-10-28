using Stomme.Engine.Extensions;

namespace Stomme.Engine.Pipelines.Runtime;

public static class StepExecutionContextExtensions
{
    private static object LogPersistenceMapKey { get; } = new();

    public static bool HasLogPersistenceModeMap(this StepExecutionContext context)
    {
        return context.TransientProperties.ContainsKey(LogPersistenceMapKey);
    }
    
    public static StepLogPersistenceModeMap GetLogPersistenceModeMap(this StepExecutionContext context)
    {
        return context.TransientProperties.GetValueOrDefault(LogPersistenceMapKey, () => new StepLogPersistenceModeMap())!;
    }

    public static void SetLogPersistenceModeMap(this StepExecutionContext context, StepLogPersistenceModeMap map)
    {
        context.TransientProperties[LogPersistenceMapKey] = map;
    }
}