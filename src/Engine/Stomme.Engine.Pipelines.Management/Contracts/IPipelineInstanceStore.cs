using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Represents a store of pipeline instances.
/// </summary>
[PublicAPI]
public interface IPipelineInstanceStore
{
    /// <summary>
    /// Finds a pipeline instance matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pipeline instance.</returns>
    ValueTask<PipelineInstance?> FindAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline instances matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A paginated list of pipeline instances.</returns>
    ValueTask<Page<PipelineInstance>> FindManyAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline instances matching the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>A paginated list of pipeline instances.</returns>
    ValueTask<Page<PipelineInstance>> FindManyAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a list of pipeline instances matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of pipeline instances.</returns>
    ValueTask<IEnumerable<PipelineInstance>> FindManyAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a list of pipeline instances matching the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>A list of pipeline instances.</returns>
    ValueTask<IEnumerable<PipelineInstance>> FindManyAsync<TOrderBy>(PipelineInstanceFilter filter, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count the number of pipeline instances matching the specified filter. 
    /// </summary>
    ValueTask<long> CountAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline instance summaries matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A paginated list of pipeline instance summaries.</returns>
    ValueTask<Page<PipelineInstanceSummary>> SummarizeManyAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline instance summaries matching the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>A paginated list of pipeline instance summaries.</returns>
    ValueTask<Page<PipelineInstanceSummary>> SummarizeManyAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a list of pipeline instance IDs matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of pipeline instance IDs.</returns>
    ValueTask<IEnumerable<string>> FindManyIdsAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline instance IDs matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A paginated list of pipeline instance IDs.</returns>
    ValueTask<Page<string>> FindManyIdsAsync(PipelineInstanceFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline instance IDs matching the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>A paginated list of pipeline instance IDs.</returns>
    ValueTask<Page<string>> FindManyIdsAsync<TOrderBy>(PipelineInstanceFilter filter, PageArgs pageArgs, PipelineInstanceOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a list of pipeline instance summaries matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of pipeline instance summaries.</returns>
    ValueTask<IEnumerable<PipelineInstanceSummary>> SummarizeManyAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Returns a list of pipeline instance summaries matching the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrder">The type of the property to order by.</typeparam>
    /// <returns>A list of pipeline instance summaries.</returns>
    ValueTask<IEnumerable<PipelineInstanceSummary>> SummarizeManyAsync<TOrder>(PipelineInstanceFilter filter, PipelineInstanceOrder<TOrder> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates the specified <see cref="PipelineInstance"/> in the persistence store.
    /// </summary>
    /// <remarks>
    /// If the record does not already exist, it is added to the store; if it does exist, its existing entry is updated.
    /// </remarks>
    /// <param name="instance">The pipeline instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask SaveAsync(PipelineInstance instance, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Adds the specified <see cref="PipelineInstance"/> in the persistence store.
    /// </summary>
    /// <param name="instance">The pipeline instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask AddAsync(PipelineInstance instance, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Updates the specified <see cref="PipelineInstance"/> in the persistence store.
    /// </summary>
    /// <param name="instance">The pipeline instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask UpdateAsync(PipelineInstance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates the specified set of <see cref="PipelineInstance"/> objects in the persistence store.
    /// </summary>
    /// <param name="instances">The pipeline instances.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// If the record does not already exist, it is added to the store; if it does exist, its existing entry is updated.
    /// </remarks>
    ValueTask SaveManyAsync(IEnumerable<PipelineInstance> instances, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all pipeline instances matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted pipeline instances.</returns>
    ValueTask<long> DeleteAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the "LastUpdated" timestamp of a pipeline instance.
    /// </summary>
    /// <param name="pipelineInstanceId">The unique identifier of the pipeline instance.</param>
    /// <param name="value">The new timestamp value to set.</param>
    /// <param name="cancellationToken">The cancellation token to observe during the operation.</param>
    Task UpdateUpdatedTimestampAsync(string pipelineInstanceId, DateTimeOffset value, CancellationToken cancellationToken = default);
}