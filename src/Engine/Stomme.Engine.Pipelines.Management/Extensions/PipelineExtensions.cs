using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Steps;
using JetBrains.Annotations;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides a set of extension methods for <see cref="Pipeline"/>.
/// </summary>
[PublicAPI]
public static class PipelineExtensions
{
    /// <summary>
    /// Returns true if the specified pipeline matches the specified version options.
    /// </summary>
    /// <param name="pipeline">The pipeline to check.</param>
    /// <param name="version">The version options.</param>
    /// <returns>True if the pipeline matches the version options; otherwise, false.</returns>
    public static bool WithVersion(this Pipeline pipeline, VersionOptions version)
    {
        var identity = pipeline.Identity;
        var (isLatest, isPublished) = pipeline.Publication;

        if (version.IsDraft)
            return !isPublished;
        if (version.IsLatest)
            return isLatest;
        if (version.IsPublished)
            return isPublished;
        if (version.IsLatestOrPublished)
            return isPublished || isLatest;
        if (version.AllVersions)
            return true;
        if (version.Version > 0)
            return identity.Version == version.Version;
        return true;
    }

    /// <summary>
    /// Applies the specified version options to the query.
    /// </summary>
    /// <param name="query">The query to apply the version options to.</param>
    /// <param name="version">The version options.</param>
    /// <returns>The query.</returns>
    public static IEnumerable<Pipeline> WithVersion(
        this IEnumerable<Pipeline> query,
        VersionOptions version) =>
        query.Where(x => x.WithVersion(version)).OrderByDescending(x => x.Identity.Version);

    /// <summary>
    /// Applies the specified version options to the query.
    /// </summary>
    /// <param name="query">The query to apply the version options to.</param>
    /// <param name="version">The version options.</param>
    /// <returns>The query.</returns>
    public static IQueryable<Pipeline> WithVersion(
        this IQueryable<Pipeline> query,
        VersionOptions version) =>
        query.Where(x => x.WithVersion(version)).OrderByDescending(x => x.Identity.Version);
}