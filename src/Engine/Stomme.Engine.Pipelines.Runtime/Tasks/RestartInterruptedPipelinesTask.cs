using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Common.RecurringTasks;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime.Options;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime.Tasks;

[SingleNodeTask]
[UsedImplicitly]
public class RestartInterruptedPipelinesTask(
    IPipelineRestarter pipelineRestarter, 
    IPipelineInstanceStore pipelineInstanceStore,
    ILogger<RestartInterruptedPipelinesTask> logger,
    IOptions<RuntimeOptions> options, 
    ISystemClock systemClock,
    ITenantService? tenantService = null,
    ITenantAccessor? tenantAccessor = null) : RecurringTask
{
    /// <inheritdoc />
    public override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var pipelineInstanceFilter = CreatePipelineInstanceFilter();
        var batchSize = options.Value.RestartInterruptedPipelinesBatchSize;
        var pipelineInstances = pipelineInstanceStore.EnumerateSummariesAsync(pipelineInstanceFilter, batchSize, cancellationToken);

        logger.LogInformation("Restarting interrupted pipelines.");
        await foreach (var pipelineInstance in pipelineInstances.ConfigureAwait(false))
        {
            try
            {
                var tenantId = pipelineInstance.TenantId ?? string.Empty;

                if (tenantService != null && tenantAccessor != null && !string.IsNullOrWhiteSpace(tenantId) && tenantId != Tenant.AgnosticTenantId)
                {
                    var tenant = await tenantService.FindAsync(tenantId, cancellationToken).ConfigureAwait(false) ?? new Tenant { Id = tenantId, Name = tenantId };

                    using (tenantAccessor.PushContext(tenant))
                        await pipelineRestarter.RestartPipelineAsync(pipelineInstance.Id, cancellationToken: cancellationToken).ConfigureAwait(false);

                    continue;
                }

                await pipelineRestarter.RestartPipelineAsync(pipelineInstance.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to restart interrupted pipeline {PipelineInstanceId}", pipelineInstance.Id);
            }
        }
        logger.LogInformation("Finished restarting interrupted pipelines.");
    }

    private PipelineInstanceFilter CreatePipelineInstanceFilter()
    {
        var livenessThreshold = options.Value.InactivityThreshold;
        var now = systemClock.UtcNow;
        var cutoffTimestamp = now - livenessThreshold;
        return new()
        {
            IsExecuting = true,
            BeforeLastUpdated = cutoffTimestamp
        };
    }
}