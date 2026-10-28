using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Requests;

/// <summary>
/// Published when bookmarks needs to be updated.
/// </summary>
/// <param name="PipelineExecutionContext">The pipeline execution context.</param>
/// <param name="Diff">A diff of the bookmarks.</param>
/// <param name="CorrelationId">The correlation ID, if any.</param>
public record UpdateBookmarksRequest(PipelineExecutionContext PipelineExecutionContext, Diff<Bookmark> Diff, string? CorrelationId = default);