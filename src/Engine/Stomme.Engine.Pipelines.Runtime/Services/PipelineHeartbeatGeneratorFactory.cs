using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;
using Stomme.Engine.Pipelines.Runtime.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime;

public class PipelineHeartbeatGeneratorFactory(IOptions<RuntimeOptions> options, ISystemClock systemClock, ILogger<PipelineHeartbeatMiddleware> logger, ILoggerFactory loggerFactory)
{
    public HeartbeatGenerator CreateHeartbeatGenerator(PipelineExecutionContext context)
    {
        var inactivityThreshold = options.Value.InactivityThreshold;
        var heartbeatInterval = TimeSpan.FromTicks((long)(inactivityThreshold.Ticks * 0.6));
        logger.LogDebug("Pipeline heartbeat interval: {Interval}", heartbeatInterval);
        return new(async () => await UpdateTimestampAsync(context).ConfigureAwait(false), heartbeatInterval, loggerFactory);
    }
    
    private async Task UpdateTimestampAsync(PipelineExecutionContext context)
    {
        var pipelineInstanceStore = context.GetRequiredService<IPipelineInstanceStore>();
        var pipelineInstanceId = context.Id;
        await pipelineInstanceStore.UpdateUpdatedTimestampAsync(pipelineInstanceId, systemClock.UtcNow, context.CancellationToken).ConfigureAwait(false);
    }
}