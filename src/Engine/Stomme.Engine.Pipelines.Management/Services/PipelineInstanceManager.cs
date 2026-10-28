using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Mappers;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class PipelineInstanceManager(
    IPipelineInstanceStore store,
    IPipelineInstanceFactory pipelineInstanceFactory,
    INotificationSender notificationSender,
    PipelineStateMapper pipelineStateMapper,
    IPipelineStateExtractor pipelineStateExtractor,
    IPipelineStateSerializer pipelineStateSerializer)
    : IPipelineInstanceManager
{
    /// <inheritdoc />
    public async Task<PipelineInstance?> FindByIdAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        return await store.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineInstance?> FindAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        return await store.FindAsync(filter, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineInstanceFilter
        {
            Id = instanceId
        };
        var count = await store.CountAsync(filter, cancellationToken).ConfigureAwait(false);
        return count > 0;
    }

    /// <inheritdoc />
    public async Task SaveAsync(PipelineInstance pipelineInstance, CancellationToken cancellationToken = default)
    {
        await store.SaveAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineInstanceSaved(pipelineInstance), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineInstance> SaveAsync(PipelineState pipelineState, CancellationToken cancellationToken)
    {
        var pipelineInstance = pipelineStateMapper.Map(pipelineState)!;
        await SaveAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        return pipelineInstance;
    }

    /// <inheritdoc />
    public async Task<PipelineInstance> SaveAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default)
    {
        var pipelineState = ExtractPipelineState(pipelineExecutionContext);
        return await SaveAsync(pipelineState, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAsync(PipelineInstance pipelineInstance, CancellationToken cancellationToken = default)
    {
        await store.AddAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineInstanceSaved(pipelineInstance), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineInstance> CreateAsync(PipelineState pipelineState, CancellationToken cancellationToken)
    {
        var pipelineInstance = pipelineStateMapper.Map(pipelineState)!;
        await CreateAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        return pipelineInstance;
    }

    public async Task<PipelineInstance> CreateAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default)
    {
        var pipelineState = ExtractPipelineState(pipelineExecutionContext);
        return await CreateAsync(pipelineState, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(PipelineInstance pipelineInstance, CancellationToken cancellationToken = default)
    {
        await store.UpdateAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineInstanceSaved(pipelineInstance), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineInstance> UpdateAsync(PipelineState pipelineState, CancellationToken cancellationToken)
    {
        var pipelineInstance = pipelineStateMapper.Map(pipelineState)!;
        await UpdateAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        return pipelineInstance;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        var instance = await store.FindAsync(filter, cancellationToken).ConfigureAwait(false);

        if (instance == null)
            return false;

        var ids = new[]
        {
            instance.Id
        };
        await notificationSender.SendAsync(new PipelineInstancesDeleting(ids), cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(filter, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineInstancesDeleted(ids), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        var summaries = (await store.SummarizeManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();
        var ids = summaries.Select(x => x.Id).ToList();
        await notificationSender.SendAsync(new PipelineInstancesDeleting(ids), cancellationToken).ConfigureAwait(false);
        var count = await store.DeleteAsync(filter, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineInstancesDeleted(ids), cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <inheritdoc />
    public PipelineState ExtractPipelineState(PipelineExecutionContext pipelineExecutionContext)
    {
        return pipelineStateExtractor.Extract(pipelineExecutionContext);
    }

    /// <inheritdoc />
    public string SerializePipelineState(PipelineState pipelineState)
    {
        return pipelineStateSerializer.Serialize(pipelineState);
    }

    /// <inheritdoc />
    public async Task<PipelineInstance> CreateAndCommitPipelineInstanceAsync(Pipeline pipeline, PipelineInstanceOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pipelineInstance = CreatePipelineInstance(pipeline, options);
        await SaveAsync(pipelineInstance, cancellationToken).ConfigureAwait(false);
        return pipelineInstance;
    }

    /// <inheritdoc />
    public PipelineInstance CreatePipelineInstance(Pipeline pipeline, PipelineInstanceOptions? options = null)
    {
        return pipelineInstanceFactory.CreatePipelineInstance(pipeline, options);
    }
}