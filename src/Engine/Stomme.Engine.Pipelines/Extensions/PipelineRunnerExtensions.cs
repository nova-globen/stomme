using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.State;
using JetBrains.Annotations;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Contains extension methods for <see cref="IPipelineRunner"/>.
/// </summary>
[PublicAPI]
public static class PipelineRunnerExtensions
{
    /// <summary>
    /// Runs a pipeline until its end, automatically resuming any bookmark it encounters.
    /// </summary>
    public static async Task<PipelineState> RunUntilEndAsync(
        this IPipelineRunner pipelineRunner,
        IStep step,
        Func<PipelineState, Bookmark, RunPipelineOptions, bool>? processBookmark = default,
        CancellationToken cancellationToken = default)
    {
        var result = await pipelineRunner.RunAsync(step, cancellationToken: cancellationToken).ConfigureAwait(false);
        var pipeline = result.Pipeline;
        var pipelineState = result.PipelineState;
        var bookmarks = new Stack<Bookmark>(pipelineState.Bookmarks);

        // Continue resuming the pipeline for as long as there are bookmarks to resume.
        while (bookmarks.TryPop(out var bookmark))
        {
            var options = new RunPipelineOptions { BookmarkId = bookmark.Id };

            // Give caller a chance to determine whether or not to resume the bookmark, as well as configure any input to provide.
            if (processBookmark != null && !processBookmark(pipelineState, bookmark, options))
                continue;

            result = await pipelineRunner.RunAsync(pipeline, pipelineState, options, cancellationToken: cancellationToken).ConfigureAwait(false);
            pipelineState = result.PipelineState;

            foreach (var newBookmark in pipelineState.Bookmarks)
                bookmarks.Push(newBookmark);
        }

        // Return the pipeline state.
        return pipelineState;
    }
}