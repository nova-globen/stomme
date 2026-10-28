using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Exceptions;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Management.Tests.Helpers;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Management.Tests.Services;

public class PipelineDefinitionServiceTests
{
    private readonly IPipelineDefinitionStore _pipelineDefinitionStore = Substitute.For<IPipelineDefinitionStore>();
    private readonly IPipelineGraphBuilder _pipelineGraphBuilder = Substitute.For<IPipelineGraphBuilder>();
    private readonly IMaterializerRegistry _materializerRegistry = Substitute.For<IMaterializerRegistry>();
    private readonly ILogger<PipelineDefinitionService> _logger = Substitute.For<ILogger<PipelineDefinitionService>>();

    [Fact]
    public async Task MaterializePipelineAsync_WithValidMaterializer_ReturnsMaterializedGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "test-materializer");
        var (_, pipelineGraph) = SetupMaterializerAndGraphBuilder(definition);

        var service = CreateService();

        // Act
        var result = await service.MaterializePipelineAsync(definition);

        // Assert
        Assert.Same(pipelineGraph, result);
    }

    [Fact]
    public async Task MaterializePipelineAsync_WithMissingMaterializer_ThrowsException()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "missing-materializer");
        _materializerRegistry.GetMaterializer("missing-materializer").Returns((IPipelineMaterializer?)null);

        var service = CreateService();

        // Act & Assert
        await Assert.ThrowsAsync<PipelineMaterializerNotFoundException>(() => service.MaterializePipelineAsync(definition));
    }

    [Fact]
    public async Task FindPipelineDefinitionAsync_ByDefinitionIdAndVersionOptions_ReturnsDefinition()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(definition);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineDefinitionAsync("def-1", VersionOptions.Published);

        // Assert
        Assert.Same(definition, result);
        await _pipelineDefinitionStore.Received(1).FindAsync(
            Arg.Any<PipelineDefinitionFilter>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPipelineDefinitionAsync_ByDefinitionVersionId_ReturnsDefinition()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        definition.Id = "version-id-1";
        _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(definition);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineDefinitionAsync("version-id-1");

        // Assert
        Assert.Same(definition, result);
        await _pipelineDefinitionStore.Received(1).FindAsync(
            Arg.Is<PipelineDefinitionFilter>(f => f.Id == "version-id-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPipelineDefinitionAsync_ByHandle_ReturnsDefinition()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var handle = PipelineDefinitionHandle.ByDefinitionId("def-1", VersionOptions.Latest);
        _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(definition);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineDefinitionAsync(handle);

        // Assert
        Assert.Same(definition, result);
        await _pipelineDefinitionStore.Received(1).FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPipelineDefinitionAsync_ByFilter_ReturnsDefinition()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var filter = new PipelineDefinitionFilter { DefinitionId = "def-1" };
        _pipelineDefinitionStore.FindAsync(filter, Arg.Any<CancellationToken>()).Returns(definition);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineDefinitionAsync(filter);

        // Assert
        Assert.Same(definition, result);
        await _pipelineDefinitionStore.Received(1).FindAsync(filter, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByDefinitionIdAndVersionOptions_WithAvailableMaterializer_ReturnsGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var pipelineGraph = SetupCompletePipelineGraphMocking(definition);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync("def-1", VersionOptions.Published);

        // Assert
        Assert.Same(pipelineGraph, result);
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByDefinitionIdAndVersionOptions_WithUnavailableMaterializer_ReturnsNull()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        SetupCompletePipelineGraphMocking(definition, materializerAvailable: false);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync("def-1", VersionOptions.Published);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByDefinitionIdAndVersionOptions_WhenDefinitionNotFound_ReturnsNull()
    {
        // Arrange
        _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns((PipelineDefinition?)null);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync("def-1", VersionOptions.Published);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByDefinitionVersionId_ReturnsGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        definition.Id = "version-id-1";
        var pipelineGraph = SetupCompletePipelineGraphMocking(definition);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync("version-id-1");

        // Assert
        Assert.Same(pipelineGraph, result);
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByHandle_ReturnsGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var handle = PipelineDefinitionHandle.ByDefinitionId("def-1", VersionOptions.Latest);
        var pipeline = new Pipeline { Identity = new("def-1", 1, "v1") };
        var pipelineGraph = TestHelpers.CreatePipelineGraph(pipeline);

        _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(definition);
        _materializerRegistry.IsMaterializerAvailable("materializer").Returns(true);

        var materializer = Substitute.For<IPipelineMaterializer>();
        materializer.MaterializeAsync(definition, Arg.Any<CancellationToken>()).Returns(pipeline);
        _materializerRegistry.GetMaterializer("materializer").Returns(materializer);
        _pipelineGraphBuilder.BuildAsync(pipeline, Arg.Any<CancellationToken>()).Returns(pipelineGraph);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync(handle);

        // Assert
        Assert.Same(pipelineGraph, result);
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByFilter_ReturnsGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var filter = new PipelineDefinitionFilter { DefinitionId = "def-1" };
        var pipeline = new Pipeline { Identity = new("def-1", 1, "v1") };
        var pipelineGraph = TestHelpers.CreatePipelineGraph(pipeline);

        _pipelineDefinitionStore.FindAsync(filter, Arg.Any<CancellationToken>()).Returns(definition);
        _materializerRegistry.IsMaterializerAvailable("materializer").Returns(true);

        var materializer = Substitute.For<IPipelineMaterializer>();
        materializer.MaterializeAsync(definition, Arg.Any<CancellationToken>()).Returns(pipeline);
        _materializerRegistry.GetMaterializer("materializer").Returns(materializer);
        _pipelineGraphBuilder.BuildAsync(pipeline, Arg.Any<CancellationToken>()).Returns(pipelineGraph);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync(filter);

        // Assert
        Assert.Same(pipelineGraph, result);
    }

    [Fact]
    public async Task FindPipelineGraphsAsync_WithMultipleDefinitions_ReturnsAllGraphs()
    {
        // Arrange
        var definition1 = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var definition2 = TestHelpers.CreatePipelineDefinition("def-2", "materializer");
        var definitions = new[] { definition1, definition2 };

        var pipeline1 = new Pipeline { Identity = new("def-1", 1, "v1") };
        var pipeline2 = new Pipeline { Identity = new("def-2", 1, "v1") };
        var graph1 = TestHelpers.CreatePipelineGraph(pipeline1);
        var graph2 = TestHelpers.CreatePipelineGraph(pipeline2);

        var filter = new PipelineDefinitionFilter();
        _pipelineDefinitionStore.FindManyAsync(filter, Arg.Any<CancellationToken>()).Returns(definitions);

        var materializer = Substitute.For<IPipelineMaterializer>();
        materializer.MaterializeAsync(definition1, Arg.Any<CancellationToken>()).Returns(pipeline1);
        materializer.MaterializeAsync(definition2, Arg.Any<CancellationToken>()).Returns(pipeline2);
        _materializerRegistry.GetMaterializer("materializer").Returns(materializer);

        _pipelineGraphBuilder.BuildAsync(pipeline1, Arg.Any<CancellationToken>()).Returns(graph1);
        _pipelineGraphBuilder.BuildAsync(pipeline2, Arg.Any<CancellationToken>()).Returns(graph2);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphsAsync(filter);

        // Assert
        var graphs = result.ToList();
        Assert.Equal(2, graphs.Count);
        Assert.Contains(graph1, graphs);
        Assert.Contains(graph2, graphs);
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByDefinitionIdAndVersionOptions_WithAvailableMaterializer_ReturnsSuccessResult()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var pipeline = new Pipeline { Identity = new("def-1", 1, "v1") };
        var pipelineGraph = TestHelpers.CreatePipelineGraph(pipeline);

        _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(definition);
        _materializerRegistry.IsMaterializerAvailable("materializer").Returns(true);

        var materializer = Substitute.For<IPipelineMaterializer>();
        materializer.MaterializeAsync(definition, Arg.Any<CancellationToken>()).Returns(pipeline);
        _materializerRegistry.GetMaterializer("materializer").Returns(materializer);
        _pipelineGraphBuilder.BuildAsync(pipeline, Arg.Any<CancellationToken>()).Returns(pipelineGraph);

        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync("def-1", VersionOptions.Published);

        // Assert
        Assert.NotNull(result);
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Same(pipelineGraph, result.PipelineGraph);
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByDefinitionIdAndVersionOptions_WithUnavailableMaterializer_ReturnsResultWithNullGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        SetupCompletePipelineGraphMocking(definition, materializerAvailable: false);

        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync("def-1", VersionOptions.Published);

        // Assert
        Assert.NotNull(result);
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Null(result.PipelineGraph);
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByDefinitionIdAndVersionOptions_WhenDefinitionNotFound_ReturnsEmptyResult()
    {
        // Arrange
        _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns((PipelineDefinition?)null);

        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync("def-1", VersionOptions.Published);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.PipelineDefinition);
        Assert.Null(result.PipelineGraph);
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByDefinitionVersionId_ReturnsResult()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        definition.Id = "version-id-1";
        var pipelineGraph = SetupCompletePipelineGraphMocking(definition);

        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync("version-id-1");

        // Assert
        Assert.NotNull(result);
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Same(pipelineGraph, result.PipelineGraph);
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByHandle_ReturnsResult()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var handle = PipelineDefinitionHandle.ByDefinitionId("def-1", VersionOptions.Latest);
        var pipeline = new Pipeline { Identity = new("def-1", 1, "v1") };
        var pipelineGraph = TestHelpers.CreatePipelineGraph(pipeline);

        _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(definition);
        _materializerRegistry.IsMaterializerAvailable("materializer").Returns(true);

        var materializer = Substitute.For<IPipelineMaterializer>();
        materializer.MaterializeAsync(definition, Arg.Any<CancellationToken>()).Returns(pipeline);
        _materializerRegistry.GetMaterializer("materializer").Returns(materializer);
        _pipelineGraphBuilder.BuildAsync(pipeline, Arg.Any<CancellationToken>()).Returns(pipelineGraph);

        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync(handle);

        // Assert
        Assert.NotNull(result);
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Same(pipelineGraph, result.PipelineGraph);
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByFilter_ReturnsResult()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var filter = new PipelineDefinitionFilter { DefinitionId = "def-1" };
        var pipeline = new Pipeline { Identity = new("def-1", 1, "v1") };
        var pipelineGraph = TestHelpers.CreatePipelineGraph(pipeline);

        _pipelineDefinitionStore.FindAsync(filter, Arg.Any<CancellationToken>()).Returns(definition);
        _materializerRegistry.IsMaterializerAvailable("materializer").Returns(true);

        var materializer = Substitute.For<IPipelineMaterializer>();
        materializer.MaterializeAsync(definition, Arg.Any<CancellationToken>()).Returns(pipeline);
        _materializerRegistry.GetMaterializer("materializer").Returns(materializer);
        _pipelineGraphBuilder.BuildAsync(pipeline, Arg.Any<CancellationToken>()).Returns(pipelineGraph);

        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync(filter);

        // Assert
        Assert.NotNull(result);
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Same(pipelineGraph, result.PipelineGraph);
    }

    [Fact]
    public async Task TryFindPipelineGraphsAsync_WithMultipleDefinitions_ReturnsAllResults()
    {
        // Arrange
        var definition1 = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var definition2 = TestHelpers.CreatePipelineDefinition("def-2", "unavailable-materializer");
        var definitions = new[] { definition1, definition2 };

        var pipeline1 = new Pipeline { Identity = new("def-1", 1, "v1") };
        var graph1 = TestHelpers.CreatePipelineGraph(pipeline1);

        var filter = new PipelineDefinitionFilter();
        _pipelineDefinitionStore.FindManyAsync(filter, Arg.Any<CancellationToken>()).Returns(definitions);

        _materializerRegistry.IsMaterializerAvailable("materializer").Returns(true);
        _materializerRegistry.IsMaterializerAvailable("unavailable-materializer").Returns(false);

        var materializer = Substitute.For<IPipelineMaterializer>();
        materializer.MaterializeAsync(definition1, Arg.Any<CancellationToken>()).Returns(pipeline1);
        _materializerRegistry.GetMaterializer("materializer").Returns(materializer);

        _pipelineGraphBuilder.BuildAsync(pipeline1, Arg.Any<CancellationToken>()).Returns(graph1);

        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphsAsync(filter);

        // Assert
        var results = result.ToList();
        Assert.Equal(2, results.Count);

        var result1 = results.First(r => r.PipelineDefinition?.DefinitionId == "def-1");
        Assert.Same(definition1, result1.PipelineDefinition);
        Assert.Same(graph1, result1.PipelineGraph);

        var result2 = results.First(r => r.PipelineDefinition?.DefinitionId == "def-2");
        Assert.Same(definition2, result2.PipelineDefinition);
        Assert.Null(result2.PipelineGraph);
    }

    private PipelineDefinitionService CreateService()
    {
        return new(
            _pipelineDefinitionStore,
            _pipelineGraphBuilder,
            _materializerRegistry,
            _logger);
    }

    /// <summary>
    /// Sets up a materializer and graph builder for the given definition.
    /// Returns the pipeline and pipeline graph that were configured.
    /// </summary>
    private (Pipeline, PipelineGraph) SetupMaterializerAndGraphBuilder(PipelineDefinition definition)
    {
        var pipeline = new Pipeline { Identity = new(definition.DefinitionId, 1, "v1") };
        var pipelineGraph = TestHelpers.CreatePipelineGraph(pipeline);

        var materializer = Substitute.For<IPipelineMaterializer>();
        materializer.MaterializeAsync(definition, Arg.Any<CancellationToken>()).Returns(pipeline);

        _materializerRegistry.GetMaterializer(definition.MaterializerName).Returns(materializer);
        _pipelineGraphBuilder.BuildAsync(pipeline, Arg.Any<CancellationToken>()).Returns(pipelineGraph);

        return (pipeline, pipelineGraph);
    }

    /// <summary>
    /// Sets up complete mocking chain for pipeline graph retrieval including definition store, materializer, and graph builder.
    /// Returns the pipeline graph that was configured.
    /// </summary>
    private PipelineGraph SetupCompletePipelineGraphMocking(
        PipelineDefinition definition,
        bool materializerAvailable = true,
        PipelineDefinitionFilter? filter = null)
    {
        var pipeline = new Pipeline { Identity = new(definition.DefinitionId, 1, "v1") };
        var pipelineGraph = TestHelpers.CreatePipelineGraph(pipeline);

        if (filter != null)
        {
            _pipelineDefinitionStore.FindAsync(filter, Arg.Any<CancellationToken>()).Returns(definition);
        }
        else
        {
            _pipelineDefinitionStore.FindAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
                .Returns(definition);
        }

        _materializerRegistry.IsMaterializerAvailable(definition.MaterializerName).Returns(materializerAvailable);

        if (materializerAvailable)
        {
            var materializer = Substitute.For<IPipelineMaterializer>();
            materializer.MaterializeAsync(definition, Arg.Any<CancellationToken>()).Returns(pipeline);
            _materializerRegistry.GetMaterializer(definition.MaterializerName).Returns(materializer);
            _pipelineGraphBuilder.BuildAsync(pipeline, Arg.Any<CancellationToken>()).Returns(pipelineGraph);
        }

        return pipelineGraph;
    }
}
