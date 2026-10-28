using System.Diagnostics.CodeAnalysis;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management.Stores;

/// <summary>
/// A non-persistent memory store for saving and loading <see cref="PipelineInstance"/> entities.
/// </summary>
public class MemoryPipelineInstanceStore : IPipelineInstanceStore
{
    private readonly MemoryStore<PipelineInstance> _store;

    /// <summary>
    /// Constructor.
    /// </summary>
    public MemoryPipelineInstanceStore(MemoryStore<PipelineInstance> store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public ValueTask<PipelineInstance?> FindAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        var entity = _store.Query(query => Filter(query, filter)).FirstOrDefault();
        return ValueTask.FromResult(entity);
    }

    /// <inheritdoc />
    public ValueTask<Page<PipelineInstance>> FindManyAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var count = _store.Query(query => Filter(query, filter)).LongCount();
        var entities = _store.Query(query => Filter(query, filter).Paginate(pageArgs)).ToList();
        var page = Page.Of(entities, count);
        return ValueTask.FromResult(page);
    }

    /// <inheritdoc />
    public ValueTask<Page<PipelineInstance>> FindManyAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var count = _store.Query(query => Filter(query, filter)).LongCount();
        var entities = _store.Query(query => Filter(query, filter).OrderBy(order).Paginate(pageArgs)).ToList();
        var page = Page.Of(entities, count);
        return ValueTask.FromResult(page);
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<PipelineInstance>> FindManyAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        var entities = _store.Query(query => Filter(query, filter)).ToList().AsEnumerable();
        return ValueTask.FromResult(entities);
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<PipelineInstance>> FindManyAsync<TOrderBy>(PipelineInstanceFilter filter, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var entities = _store.Query(query => Filter(query, filter).OrderBy(order)).ToList().AsEnumerable();
        return ValueTask.FromResult(entities);
    }

    /// <inheritdoc />
    public ValueTask<long> CountAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        var count = _store.Query(query => Filter(query, filter)).LongCount();
        return new(count);
    }

    /// <inheritdoc />
    public async ValueTask<Page<PipelineInstanceSummary>> SummarizeManyAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var page = await FindManyAsync(filter, pageArgs, cancellationToken).ConfigureAwait(false);
        return new(page.Items.Select(PipelineInstanceSummary.FromInstance).ToList(), page.TotalCount);
    }

    /// <inheritdoc />
    public async ValueTask<Page<PipelineInstanceSummary>> SummarizeManyAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var page = await FindManyAsync(filter, pageArgs, order, cancellationToken).ConfigureAwait(false);
        return new(page.Items.Select(PipelineInstanceSummary.FromInstance).ToList(), page.TotalCount);
    }

    /// <inheritdoc />
    public async ValueTask<IEnumerable<PipelineInstanceSummary>> SummarizeManyAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        var entities = await FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
        return entities.Select(PipelineInstanceSummary.FromInstance);
    }

    /// <inheritdoc />
    public async ValueTask<IEnumerable<PipelineInstanceSummary>> SummarizeManyAsync<TOrderBy>(PipelineInstanceFilter filter, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var entities = await FindManyAsync(filter, order, cancellationToken).ConfigureAwait(false);
        return entities.Select(PipelineInstanceSummary.FromInstance);
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<string>> FindManyIdsAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        var entities = _store.Query(query => Filter(query, filter)).Select(x => x.Id).ToList().AsEnumerable();
        return ValueTask.FromResult(entities);
    }

    /// <inheritdoc />
    public async ValueTask<Page<string>> FindManyIdsAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var page = await FindManyAsync(filter, pageArgs, cancellationToken).ConfigureAwait(false);
        var ids = page.Items.Select(x => x.Id).ToList();
        return new(ids, page.TotalCount);
    }

    /// <inheritdoc />
    public async ValueTask<Page<string>> FindManyIdsAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var page = await FindManyAsync(filter, pageArgs, order, cancellationToken).ConfigureAwait(false);
        var ids = page.Items.Select(x => x.Id).ToList();
        return new(ids, page.TotalCount);
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(PipelineInstance instance, CancellationToken cancellationToken = default)
    {
        _store.Save(instance, x => x.Id);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddAsync(PipelineInstance instance, CancellationToken cancellationToken = default)
    {
        _store.Add(instance, GetId);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync(PipelineInstance instance, CancellationToken cancellationToken = default)
    {
        _store.Update(instance, GetId);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SaveManyAsync(IEnumerable<PipelineInstance> instances, CancellationToken cancellationToken = default)
    {
        _store.SaveMany(instances, GetId);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<long> DeleteAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default)
    {
        var query = Filter(_store.List().AsQueryable(), filter);
        var count = _store.DeleteMany(query, x => x.Id);
        return ValueTask.FromResult(count);
    }

    /// <inheritdoc />
    public async Task UpdateUpdatedTimestampAsync(string pipelineInstanceId, DateTimeOffset value, CancellationToken cancellationToken = default)
    {
        var pipelineInstance = await FindAsync(new()
        {
            Id = pipelineInstanceId
        }, cancellationToken).ConfigureAwait(false);
        
        if (pipelineInstance == null)
            throw new InvalidOperationException($"Pipeline instance with ID '{pipelineInstanceId}' does not exist.");
        
        pipelineInstance.UpdatedAt = value;
    }

    private static string GetId(PipelineInstance pipelineInstance) => pipelineInstance.Id;

    [RequiresUnreferencedCode("Calls Stomme.Engine.Pipelines.Management.Filters.PipelineInstanceFilter.Apply(IQueryable<PipelineInstance>)")]
    private static IQueryable<PipelineInstance> Filter(IQueryable<PipelineInstance> query, PipelineInstanceFilter filter) => filter.Apply(query);
}