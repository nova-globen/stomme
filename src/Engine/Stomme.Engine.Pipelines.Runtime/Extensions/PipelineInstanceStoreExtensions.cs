using System.Runtime.CompilerServices;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Runtime;

public static class PipelineInstanceStoreExtensions
{
    public static async IAsyncEnumerable<PipelineInstanceSummary> EnumerateSummariesAsync(
        this IPipelineInstanceStore store, 
        PipelineInstanceFilter filter, 
        int batchSize = 100, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pageArgs = PageArgs.FromPage(0, batchSize);

        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await store.SummarizeManyAsync(filter, pageArgs, cancellationToken).ConfigureAwait(false);
            var pipelineInstances = page.Items;

            if (pipelineInstances.Count == 0)
                yield break;

            foreach (var pipelineInstance in pipelineInstances)
                yield return pipelineInstance;

            pageArgs = pageArgs.Next();
        }
    }
}