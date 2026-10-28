using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides a set of extension methods for <see cref="IPipelineInstanceStore"/>.
/// </summary>
public static class PipelineInstanceStoreExtensions
{
    /// <summary>
    /// Finds a pipeline instance matching the specified ID.
    /// </summary>
    public static async ValueTask<PipelineInstance?> FindAsync(this IPipelineInstanceStore store, string id, CancellationToken cancellationToken = default)
    {
        return await store.FindAsync(new() { Id = id }, cancellationToken).ConfigureAwait(false);
    }
}