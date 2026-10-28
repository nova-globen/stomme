using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management.Stores;

/// <summary>
/// A memory implementation of <see cref="IPipelineDefinitionStore"/>.
/// </summary>
public class MemoryPipelineDefinitionStore(MemoryStore<PipelineDefinition> store) : IPipelineDefinitionStore
{
    /// <inheritdoc />
    public Task<PipelineDefinition?> FindAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var result = store.Query(query => Filter(query, filter)).FirstOrDefault();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<PipelineDefinition?> FindAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var result = store.Query(query => Filter(query, filter).OrderBy(order)).FirstOrDefault();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<Page<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var count = store.Query(query => Filter(query, filter)).LongCount();
        var result = store.Query(query => Filter(query, filter).Paginate(pageArgs)).ToList();
        return Task.FromResult(Page.Of(result, count));
    }

    /// <inheritdoc />
    public Task<Page<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var count = store.Query(query => Filter(query, filter).OrderBy(order)).LongCount();
        var result = store.Query(query => Filter(query, filter).Paginate(pageArgs)).ToList();
        return Task.FromResult(Page.Of(result, count));
    }

    /// <inheritdoc />
    public Task<IEnumerable<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var result = store.Query(query => Filter(query, filter)).ToList().AsEnumerable();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var result = store.Query(query => Filter(query, filter).OrderBy(order)).ToList().AsEnumerable();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<Page<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var count = store.Query(query => Filter(query, filter)).LongCount();
        var result = store.Query(query => Filter(query, filter).Paginate(pageArgs)).Select(PipelineDefinitionSummary.FromDefinition).ToList();
        return Task.FromResult(Page.Of(result, count));
    }

    /// <inheritdoc />
    public Task<Page<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var count = store.Query(query => Filter(query, filter).OrderBy(order)).LongCount();
        var result = store.Query(query => Filter(query, filter).Paginate(pageArgs)).Select(PipelineDefinitionSummary.FromDefinition).ToList();
        return Task.FromResult(Page.Of(result, count));
    }

    /// <inheritdoc />
    public Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var result = store.Query(query => Filter(query, filter)).Select(PipelineDefinitionSummary.FromDefinition).ToList().AsEnumerable();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var result = store.Query(query => Filter(query, filter).OrderBy(order)).Select(PipelineDefinitionSummary.FromDefinition).ToList().AsEnumerable();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<PipelineDefinition?> FindLastVersionAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken)
    {
        var result = store.Query(query => Filter(query, filter)).MaxBy(x => x.Version);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task SaveAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        store.Save(definition, GetId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveManyAsync(IEnumerable<PipelineDefinition> definitions, CancellationToken cancellationToken = default)
    {
        store.SaveMany(definitions, GetId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<long> DeleteAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var pipelineDefinitionIds = store.Query(query => Filter(query, filter)).Select(x => x.DefinitionId).Distinct().ToList();
        store.DeleteWhere(x => pipelineDefinitionIds.Contains(x.DefinitionId));
        return Task.FromResult(pipelineDefinitionIds.LongCount());
    }

    /// <inheritdoc />
    public Task<bool> AnyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var exists = store.Query(query => Filter(query, filter)).Any();
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public Task<long> CountDistinctAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(store.Count(x => true, x => x.DefinitionId));
    }

    /// <inheritdoc />
    public Task<bool> GetIsNameUnique(string name, string? definitionId = default, CancellationToken cancellationToken = default)
    {
        var exists = store.Any(x => x.Name == name && x.DefinitionId != definitionId);
        return Task.FromResult(!exists);
    }

    private IQueryable<PipelineDefinition> Filter(IQueryable<PipelineDefinition> queryable, PipelineDefinitionFilter filter) => filter.Apply(queryable);

    private string GetId(PipelineDefinition pipelineDefinition) => pipelineDefinition.Id;
}