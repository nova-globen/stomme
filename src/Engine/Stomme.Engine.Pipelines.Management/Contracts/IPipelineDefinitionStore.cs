using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Represents a store of <see cref="PipelineDefinition"/>s.
/// </summary>
public interface IPipelineDefinitionStore
{
    /// <summary>
    /// Finds a pipeline definition using the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pipeline definition.</returns>
    Task<PipelineDefinition?> FindAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a pipeline definition using the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>The pipeline definition.</returns>
    Task<PipelineDefinition?> FindAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline definitions using the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A paginated list of pipeline definitions.</returns>
    Task<Page<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline definitions using the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="order">The order.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>A paginated list of pipeline definitions.</returns>
    Task<Page<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a list of pipeline definitions using the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of pipeline definitions.</returns>
    Task<IEnumerable<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a list of pipeline definitions using the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>A list of pipeline definitions.</returns>
    Task<IEnumerable<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline definition summaries using the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A paginated list of pipeline definition summaries.</returns>
    Task<Page<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of pipeline definition summaries using the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="order">The order.</param>
    /// <param name="pageArgs">The page arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>A paginated list of pipeline definition summaries.</returns>
    Task<Page<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a list of pipeline definition summaries using the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of pipeline definition summaries.</returns>
    Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a list of pipeline definition summaries using the specified filter and order.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="order">The order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the property to order by.</typeparam>
    /// <returns>A list of pipeline definition summaries.</returns>
    Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest version of the pipeline definition matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pipeline definition.</returns>
    Task<PipelineDefinition?> FindLastVersionAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Adds or updates the specified <see cref="PipelineDefinition"/> in the persistence store.
    /// </summary>
    /// <param name="definition">The pipeline definition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// If the record does not already exist, it is added to the store; if it does exist, its existing entry is updated.
    /// </remarks>
    Task SaveAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the specified set of <see cref="PipelineDefinition"/> objects to te persistence store.
    /// </summary>
    /// <param name="definitions">The pipeline definitions.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// If the record does not already exist, it is added to the store; if it does exist, its existing entry is updated.
    /// </remarks>
    Task SaveManyAsync(IEnumerable<PipelineDefinition> definitions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all pipeline definitions matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of pipeline definitions deleted.</returns>
    Task<long> DeleteAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if any pipeline definition matches the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if any pipeline definition matches the specified filter.</returns>
    Task<bool> AnyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the number of logical pipeline definitions.
    /// </summary>
    Task<long> CountDistinctAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value indicating whether the specified name is unique.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="definitionId">The definition ID to exclude from the check.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<bool> GetIsNameUnique(string name, string? definitionId = null, CancellationToken cancellationToken = default);
}