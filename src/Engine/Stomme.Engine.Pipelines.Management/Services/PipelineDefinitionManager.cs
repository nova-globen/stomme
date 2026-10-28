using Stomme.Engine.Common.Models;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Notifications;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class PipelineDefinitionManager(
    IPipelineDefinitionStore store,
    INotificationSender notificationSender,
    IPipelineDefinitionPublisher pipelinePublisher) : IPipelineDefinitionManager
{
    /// <inheritdoc />
    public async Task<long> DeleteByDefinitionIdAsync(string definitionId, CancellationToken cancellationToken = default)
    {
        await notificationSender.SendAsync(new PipelineDefinitionDeleting(definitionId), cancellationToken).ConfigureAwait(false);
        var filter = new PipelineDefinitionFilter
        {
            DefinitionId = definitionId
        };
        var count = await store.DeleteAsync(filter, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineDefinitionDeleted(definitionId), cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineDefinitionFilter
        {
            Id = id
        };
        var definition = await store.FindAsync(filter, cancellationToken).ConfigureAwait(false);

        if (definition == null)
            return false;

        return await DeleteVersionAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteByDefinitionIdsAsync(IEnumerable<string> definitionIds, CancellationToken cancellationToken = default)
    {
        var definitionIdList = definitionIds.Distinct().ToList();
        await notificationSender.SendAsync(new PipelineDefinitionsDeleting(definitionIdList), cancellationToken).ConfigureAwait(false);
        var filter = new PipelineDefinitionFilter
        {
            DefinitionIds = definitionIdList,
            IsReadonly = false
        };
        var count = await store.DeleteAsync(filter, cancellationToken).ConfigureAwait(false);
        await EnsureLastVersionIsLatestAsync(definitionIdList, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineDefinitionsDeleted(definitionIdList), cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteByIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        var definitions = await store.FindSummariesAsync(new PipelineDefinitionFilter
        {
            Ids = idList
        }, cancellationToken).ConfigureAwait(false);
        var definitionIds = definitions.Select(x => x.DefinitionId).Distinct().ToList();
        await notificationSender.SendAsync(new PipelineDefinitionVersionsDeleting(idList), cancellationToken).ConfigureAwait(false);
        var filter = new PipelineDefinitionFilter
        {
            Ids = idList
        };
        var count = await store.DeleteAsync(filter, cancellationToken).ConfigureAwait(false);
        await EnsureLastVersionIsLatestAsync(definitionIds, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineDefinitionVersionsDeleted(idList), cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteVersionAsync(string definitionId, int versionToDelete, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineDefinitionFilter
        {
            DefinitionId = definitionId,
            VersionOptions = VersionOptions.SpecificVersion(versionToDelete)
        };
        var definitionToDelete = await store.FindAsync(filter, cancellationToken).ConfigureAwait(false);

        if (definitionToDelete == null)
            return false;

        return await DeleteVersionAsync(definitionToDelete, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteVersionAsync(PipelineDefinition definitionToDelete, CancellationToken cancellationToken)
    {
        if (definitionToDelete.IsPublished)
        {
            await pipelinePublisher.RetractAsync(definitionToDelete, cancellationToken).ConfigureAwait(false);
        }

        await notificationSender.SendAsync(new PipelineDefinitionVersionDeleting(definitionToDelete), cancellationToken).ConfigureAwait(false);

        var filter = new PipelineDefinitionFilter
        {
            Id = definitionToDelete.Id
        };
        var isDeleted = await store.DeleteAsync(filter, cancellationToken).ConfigureAwait(false) > 0;

        if (!isDeleted)
            return false;

        await EnsureLastVersionIsLatestAsync(definitionToDelete.DefinitionId, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineDefinitionVersionDeleted(definitionToDelete), cancellationToken).ConfigureAwait(false);

        return isDeleted;
    }

    /// <inheritdoc />
    public Task<PipelineDefinition> RevertVersionAsync(string definitionId, int version, CancellationToken cancellationToken = default)
    {
        return pipelinePublisher.RevertVersionAsync(definitionId, version, cancellationToken);
    }

    private async Task EnsureLastVersionIsLatestAsync(IEnumerable<string> definitionIds, CancellationToken cancellationToken)
    {
        foreach (var definitionId in definitionIds)
            await EnsureLastVersionIsLatestAsync(definitionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures that the last version of a pipeline definition is marked as "IsLatest = true".
    /// </summary>
    /// <param name="definitionId">The definition ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task EnsureLastVersionIsLatestAsync(string definitionId, CancellationToken cancellationToken)
    {
        var filter = new PipelineDefinitionFilter
        {
            DefinitionId = definitionId
        };
        var lastVersion = await store.FindLastVersionAsync(filter, cancellationToken).ConfigureAwait(false);

        if (lastVersion is null)
            return;

        lastVersion.IsLatest = true;
        await store.SaveAsync(lastVersion, cancellationToken).ConfigureAwait(false);
    }
}