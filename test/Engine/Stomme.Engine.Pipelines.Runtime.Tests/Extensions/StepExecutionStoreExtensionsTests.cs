using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Stores;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Extensions;

public class StepExecutionStoreExtensionsTests
{
    [Fact(DisplayName = "GetExecutionChainAsync returns empty page when record not found")]
    public async Task GetExecutionChainAsync_ReturnsEmptyPage_WhenRecordNotFound()
    {
        // Arrange
        var store = CreateStore();

        // Act
        var result = await store.GetExecutionChainAsync("non-existent-id");

        // Assert
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact(DisplayName = "GetExecutionChainAsync returns single record when no parent exists")]
    public async Task GetExecutionChainAsync_ReturnsSingleRecord_WhenNoParent()
    {
        // Arrange
        var store = CreateStore(CreateChain(1));

        // Act & Assert
        await AssertChainAsync(store, "record-1", expectedIds: ["record-1"]);
    }

    [Fact(DisplayName = "GetExecutionChainAsync traverses chain correctly with multiple levels")]
    public async Task GetExecutionChainAsync_TraversesChainCorrectly_WithMultipleLevels()
    {
        // Arrange
        var store = CreateStore(CreateChain(3));

        // Act & Assert
        await AssertChainAsync(store, "record-3", expectedIds: ["record-1", "record-2", "record-3"]);
    }

    [Theory(DisplayName = "GetExecutionChainAsync respects pipeline boundary based on includeCrossPipelineChain")]
    [InlineData(false, new[] { "child-root", "child-leaf" })]
    [InlineData(true, new[] { "parent-record", "child-root", "child-leaf" })]
    public async Task GetExecutionChainAsync_RespectsPipelineBoundary(bool includeCrossPipelineChain, string[] expectedIds)
    {
        // Arrange: Create records across pipeline boundaries
        var parentRecord = CreateRecord("parent-record", "parent-pipeline", null);
        var childRoot = CreateRecord("child-root", "child-pipeline", "parent-record", "parent-pipeline");
        var childLeaf = CreateRecord("child-leaf", "child-pipeline", "child-root");
        var store = CreateStore(parentRecord, childRoot, childLeaf);

        // Act & Assert
        await AssertChainAsync(store, "child-leaf", expectedIds, includeCrossPipelineChain: includeCrossPipelineChain);
    }

    [Theory(DisplayName = "GetExecutionChainAsync applies pagination correctly")]
    [InlineData(0, null, 4, new[] { "record-1", "record-2", "record-3", "record-4" })] // No pagination
    [InlineData(2, null, 4, new[] { "record-3", "record-4" })]                          // Skip only
    [InlineData(0, 2, 4, new[] { "record-1", "record-2" })]                             // Take only
    [InlineData(1, 2, 4, new[] { "record-2", "record-3" })]                             // Skip and take
    public async Task GetExecutionChainAsync_AppliesPagination(int skip, int? take, int expectedTotal, string[] expectedIds)
    {
        // Arrange
        var store = CreateStore(CreateChain(4));

        // Act
        var result = await store.GetExecutionChainAsync("record-4", skip: skip, take: take);
        var items = result.Items.ToList();

        // Assert
        Assert.Equal(expectedTotal, result.TotalCount);
        Assert.Equal(expectedIds, items.Select(x => x.Id).ToArray());
    }

    [Fact(DisplayName = "GetExecutionChainAsync handles circular reference gracefully")]
    public async Task GetExecutionChainAsync_HandlesCircularReference_Gracefully()
    {
        // Arrange: Create a circular reference (which shouldn't happen in practice, but should be handled)
        var record1 = CreateRecord("record-1", "pipeline-1", "record-2");
        var record2 = CreateRecord("record-2", "pipeline-1", "record-1");
        var store = CreateStore(record1, record2);

        // Act
        var result = await store.GetExecutionChainAsync("record-1");

        // Assert: Should have visited both records (once each) and stopped
        Assert.Equal(2, result.Items.Count);
    }

    #region Helpers

    private static MemoryStepExecutionStore CreateStore(params StepExecutionRecord[] records)
    {
        var memoryStore = new MemoryStore<StepExecutionRecord>();
        memoryStore.SaveMany(records, r => r.Id);
        return new MemoryStepExecutionStore(memoryStore);
    }

    private static async Task AssertChainAsync(
        IStepExecutionStore store,
        string startRecordId,
        string[] expectedIds,
        bool includeCrossPipelineChain = true,
        int skip = 0,
        int? take = null)
    {
        var result = await store.GetExecutionChainAsync(startRecordId, includeCrossPipelineChain, skip, take);
        var items = result.Items.ToList();

        Assert.Equal(expectedIds.Length, items.Count);
        Assert.Equal(expectedIds, items.Select(x => x.Id).ToArray());
        Assert.True(result.TotalCount >= expectedIds.Length);
    }

    private static StepExecutionRecord[] CreateChain(int length, string pipelineId = "pipeline-1")
    {
        var records = new StepExecutionRecord[length];
        for (var i = 0; i < length; i++)
        {
            var id = $"record-{i + 1}";
            var parentId = i > 0 ? $"record-{i}" : null;
            records[i] = CreateRecord(id, pipelineId, parentId);
        }
        return records;
    }

    private static StepExecutionRecord CreateRecord(
        string id,
        string pipelineInstanceId,
        string? schedulingStepExecutionId,
        string? schedulingPipelineInstanceId = null) =>
        new()
        {
            Id = id,
            PipelineInstanceId = pipelineInstanceId,
            StepId = $"step-{id}",
            StepNodeId = $"node-{id}",
            StepType = "TestStep",
            StepTypeVersion = 1,
            StartedAt = DateTimeOffset.UtcNow,
            Status = StepStatus.Completed,
            SchedulingStepExecutionId = schedulingStepExecutionId,
            SchedulingPipelineInstanceId = schedulingPipelineInstanceId
        };

    #endregion
}
