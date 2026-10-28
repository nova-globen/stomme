using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Extensions;

public class PipelineInstanceStoreExtensionsTests
{
    [Fact]
    public async Task EnumerateSummariesAsync_AdvancesAcrossPagesWithoutRepeatingOffsets()
    {
        // Arrange
        var store = Substitute.For<IPipelineInstanceStore>();
        var filter = new PipelineInstanceFilter();
        var requestedPages = new List<PageArgs>();
        var page1 = new List<PipelineInstanceSummary>
        {
            CreateSummary("pipeline-1"),
            CreateSummary("pipeline-2")
        };
        var page2 = new List<PipelineInstanceSummary>
        {
            CreateSummary("pipeline-3"),
            CreateSummary("pipeline-4")
        };

        store
            .SummarizeManyAsync(Arg.Any<PipelineInstanceFilter>(), Arg.Any<PageArgs>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var pageArgs = callInfo.ArgAt<PageArgs>(1);
                requestedPages.Add(pageArgs with { });

                var items = pageArgs.Offset switch
                {
                    0 => page1,
                    2 => page2,
                    _ => new List<PipelineInstanceSummary>()
                };

                return new ValueTask<Page<PipelineInstanceSummary>>(Page.Of(items, page1.Count + page2.Count));
            });

        // Act
        var results = new List<PipelineInstanceSummary>();
        await foreach (var pipelineInstance in store.EnumerateSummariesAsync(filter, 2, CancellationToken.None))
            results.Add(pipelineInstance);

        // Assert
        Assert.Equal(["pipeline-1", "pipeline-2", "pipeline-3", "pipeline-4"], results.Select(x => x.Id).ToArray());
        Assert.Equal([(int?)0, 2, 4], requestedPages.Select(x => x.Offset).ToArray());
        Assert.All(requestedPages, x => Assert.Equal(2, x.Limit));
    }

    private static PipelineInstanceSummary CreateSummary(string id) => new()
    {
        Id = id,
        DefinitionId = "definition",
        DefinitionVersionId = "definition:1"
    };
}