using Stomme.Engine.Caching;
using Stomme.Engine.Caching.Options;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Management.Tests.Helpers;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Management.Tests.Services;

public class CachingPipelineDefinitionServiceTests
{
    private const string DefaultDefinitionId = "def-1";
    private const string DefaultVersionId = "version-id-1";
    private const string DefaultMaterializerName = "materializer";

    private readonly IPipelineDefinitionService _decoratedService = Substitute.For<IPipelineDefinitionService>();
    private readonly IPipelineDefinitionCacheManager _cacheManager = Substitute.For<IPipelineDefinitionCacheManager>();
    private readonly IPipelineDefinitionStore _pipelineDefinitionStore = Substitute.For<IPipelineDefinitionStore>();
    private readonly IMaterializerRegistry _materializerRegistry = Substitute.For<IMaterializerRegistry>();
    private readonly ICacheManager _cache = Substitute.For<ICacheManager>();

    public CachingPipelineDefinitionServiceTests()
    {
        SetupCacheManager();
    }

    [Fact]
    public async Task MaterializePipelineAsync_DelegatesToDecoratedService()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        var (_, pipelineGraph) = CreatePipelineAndGraph(DefaultDefinitionId);
        _decoratedService.MaterializePipelineAsync(definition, Arg.Any<CancellationToken>()).Returns(pipelineGraph);
        var service = CreateService();

        // Act
        var result = await service.MaterializePipelineAsync(definition);

        // Assert
        Assert.Same(pipelineGraph, result);
        await _decoratedService.Received(1).MaterializePipelineAsync(definition, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPipelineDefinitionAsync_ByDefinitionIdAndVersionOptions_CreatesCacheKey()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        _cacheManager.CreatePipelineDefinitionVersionCacheKey(DefaultDefinitionId, VersionOptions.Published).Returns("cache-key");
        _decoratedService.FindPipelineDefinitionAsync(DefaultDefinitionId, VersionOptions.Published, Arg.Any<CancellationToken>()).Returns(definition);
        var service = CreateService();

        // Act
        var result = await service.FindPipelineDefinitionAsync(DefaultDefinitionId, VersionOptions.Published);

        // Assert
        Assert.Same(definition, result);
        _cacheManager.Received(1).CreatePipelineDefinitionVersionCacheKey(DefaultDefinitionId, VersionOptions.Published);
    }

    [Fact]
    public async Task FindPipelineDefinitionAsync_ByDefinitionVersionId_CreatesCacheKey()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        definition.Id = DefaultVersionId;
        _cacheManager.CreatePipelineDefinitionVersionCacheKey(DefaultVersionId).Returns("cache-key");
        _decoratedService.FindPipelineDefinitionAsync(DefaultVersionId, Arg.Any<CancellationToken>()).Returns(definition);
        var service = CreateService();

        // Act
        var result = await service.FindPipelineDefinitionAsync(DefaultVersionId);

        // Assert
        Assert.Same(definition, result);
        _cacheManager.Received(1).CreatePipelineDefinitionVersionCacheKey(DefaultVersionId);
    }

    [Fact]
    public async Task FindPipelineDefinitionAsync_ByHandle_ConvertsTFilterAndDelegates()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var handle = PipelineDefinitionHandle.ByDefinitionId("def-1", VersionOptions.Latest);
        var cacheKey = "cache-key-filter";

        _cacheManager.CreatePipelineDefinitionFilterCacheKey(Arg.Any<PipelineDefinitionFilter>()).Returns(cacheKey);
        _decoratedService.FindPipelineDefinitionAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(definition);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineDefinitionAsync(handle);

        // Assert
        Assert.Same(definition, result);
        await _decoratedService.Received(1).FindPipelineDefinitionAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPipelineDefinitionAsync_ByFilter_CreatesCacheKey()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition("def-1", "materializer");
        var filter = new PipelineDefinitionFilter { DefinitionId = "def-1" };
        var cacheKey = "cache-key-filter";

        _cacheManager.CreatePipelineDefinitionFilterCacheKey(filter).Returns(cacheKey);
        _decoratedService.FindPipelineDefinitionAsync(filter, Arg.Any<CancellationToken>()).Returns(definition);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineDefinitionAsync(filter);

        // Assert
        Assert.Same(definition, result);
        _cacheManager.Received(1).CreatePipelineDefinitionFilterCacheKey(filter);
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByDefinitionIdAndVersionOptions_CreatesCacheKey()
    {
        // Arrange
        var (_, pipelineGraph) = CreatePipelineAndGraph("def-1");
        var cacheKey = "cache-key-graph";

        _cacheManager.CreatePipelineVersionCacheKey("def-1", VersionOptions.Published).Returns(cacheKey);
        _decoratedService.FindPipelineGraphAsync("def-1", VersionOptions.Published, Arg.Any<CancellationToken>())
            .Returns(pipelineGraph);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync("def-1", VersionOptions.Published);

        // Assert
        Assert.Same(pipelineGraph, result);
        _cacheManager.Received(1).CreatePipelineVersionCacheKey("def-1", VersionOptions.Published);
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByDefinitionVersionId_CreatesCacheKey()
    {
        // Arrange
        var (_, pipelineGraph) = CreatePipelineAndGraph("def-1");
        var cacheKey = "cache-key-graph-version";

        _cacheManager.CreatePipelineVersionCacheKey("version-id-1").Returns(cacheKey);
        _decoratedService.FindPipelineGraphAsync("version-id-1", Arg.Any<CancellationToken>()).Returns(pipelineGraph);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync("version-id-1");

        // Assert
        Assert.Same(pipelineGraph, result);
        _cacheManager.Received(1).CreatePipelineVersionCacheKey("version-id-1");
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByHandle_ConvertsTFilterAndResolvesDefinitionAndGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        definition.Id = DefaultVersionId;
        var (_, pipelineGraph) = CreatePipelineAndGraph(DefaultDefinitionId);
        var handle = PipelineDefinitionHandle.ByDefinitionId(DefaultDefinitionId, VersionOptions.Latest);

        _cacheManager.CreatePipelineDefinitionFilterCacheKey(Arg.Any<PipelineDefinitionFilter>()).Returns("cache-key-def");
        _cacheManager.CreatePipelineVersionCacheKey(DefaultVersionId).Returns("cache-key-graph");
        _decoratedService.FindPipelineDefinitionAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>()).Returns(definition);
        SetupDefinitionAndGraphMaterialization(definition, pipelineGraph);
        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync(handle);

        // Assert
        Assert.Same(pipelineGraph, result);
        await _decoratedService.Received(1).FindPipelineDefinitionAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByFilter_ResolvesDefinitionFirstAndCachesGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        definition.Id = DefaultVersionId;
        var (_, pipelineGraph) = CreatePipelineAndGraph(DefaultDefinitionId);
        var filter = new PipelineDefinitionFilter { DefinitionId = DefaultDefinitionId };

        _cacheManager.CreatePipelineDefinitionFilterCacheKey(filter).Returns("cache-key-def");
        _cacheManager.CreatePipelineVersionCacheKey(DefaultVersionId).Returns("cache-key-graph");
        _decoratedService.FindPipelineDefinitionAsync(filter, Arg.Any<CancellationToken>()).Returns(definition);
        SetupDefinitionAndGraphMaterialization(definition, pipelineGraph);
        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync(filter);

        // Assert
        Assert.Same(pipelineGraph, result);
        _cacheManager.Received(1).CreatePipelineDefinitionFilterCacheKey(filter);
        _cacheManager.Received(1).CreatePipelineVersionCacheKey(DefaultVersionId);
    }

    [Fact]
    public async Task FindPipelineGraphAsync_ByFilter_WithUnavailableMaterializer_ReturnsNull()
    {
        // Arrange
        const string unavailableMaterializer = "unavailable-materializer";
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, unavailableMaterializer);
        definition.Id = DefaultVersionId;
        var filter = new PipelineDefinitionFilter { DefinitionId = DefaultDefinitionId };

        _cacheManager.CreatePipelineDefinitionFilterCacheKey(filter).Returns("cache-key");
        _decoratedService.FindPipelineDefinitionAsync(filter, Arg.Any<CancellationToken>()).Returns(definition);
        _materializerRegistry.IsMaterializerAvailable(unavailableMaterializer).Returns(false);
        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphAsync(filter);

        // Assert
        Assert.Null(result);
        _cacheManager.Received(1).CreatePipelineDefinitionFilterCacheKey(filter);
        await _decoratedService.DidNotReceive().MaterializePipelineAsync(Arg.Any<PipelineDefinition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPipelineGraphsAsync_WithMultipleDefinitions_CachesEachGraph()
    {
        // Arrange
        var definition1 = TestHelpers.CreatePipelineDefinition("def-1", DefaultMaterializerName);
        definition1.Id = "id-1";
        var definition2 = TestHelpers.CreatePipelineDefinition("def-2", DefaultMaterializerName);
        definition2.Id = "id-2";
        var (_, graph1) = CreatePipelineAndGraph("def-1");
        var (_, graph2) = CreatePipelineAndGraph("def-2");
        var filter = new PipelineDefinitionFilter();

        _pipelineDefinitionStore.FindManyAsync(filter, Arg.Any<CancellationToken>()).Returns(new[] { definition1, definition2 });
        _cacheManager.CreatePipelineVersionCacheKey("id-1").Returns("cache-key-1");
        _cacheManager.CreatePipelineVersionCacheKey("id-2").Returns("cache-key-2");
        SetupDefinitionAndGraphMaterialization(definition1, graph1);
        SetupDefinitionAndGraphMaterialization(definition2, graph2);
        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphsAsync(filter);

        // Assert
        var graphs = result.ToList();
        Assert.Equal(2, graphs.Count);
        Assert.Contains(graph1, graphs);
        Assert.Contains(graph2, graphs);
        _cacheManager.Received(1).CreatePipelineVersionCacheKey("id-1");
        _cacheManager.Received(1).CreatePipelineVersionCacheKey("id-2");
    }

    [Fact]
    public async Task FindPipelineGraphsAsync_SkipsDefinitionsWithUnavailableMaterializer()
    {
        // Arrange
        var definition1 = TestHelpers.CreatePipelineDefinition("def-1", "available-materializer");
        definition1.Id = "id-1";
        var definition2 = TestHelpers.CreatePipelineDefinition("def-2", "unavailable-materializer");
        definition2.Id = "id-2";
        var definitions = new[] { definition1, definition2 };

        var (_, graph1) = CreatePipelineAndGraph("def-1");

        var filter = new PipelineDefinitionFilter();
        _pipelineDefinitionStore.FindManyAsync(filter, Arg.Any<CancellationToken>()).Returns(definitions);

        _cacheManager.CreatePipelineVersionCacheKey("id-1").Returns("cache-key-1");
        _materializerRegistry.IsMaterializerAvailable("available-materializer").Returns(true);
        _materializerRegistry.IsMaterializerAvailable("unavailable-materializer").Returns(false);
        _decoratedService.MaterializePipelineAsync(definition1, Arg.Any<CancellationToken>()).Returns(graph1);

        var service = CreateService();

        // Act
        var result = await service.FindPipelineGraphsAsync(filter);

        // Assert
        var graphs = result.ToList();
        Assert.Single(graphs);
        Assert.Same(graph1, graphs[0]);
        await _decoratedService.DidNotReceive().MaterializePipelineAsync(definition2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByDefinitionIdAndVersionOptions_ResolvesDefinitionAndGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        definition.Id = DefaultVersionId;
        var (_, pipelineGraph) = CreatePipelineAndGraph(DefaultDefinitionId);

        _cacheManager.CreatePipelineDefinitionVersionCacheKey(DefaultDefinitionId, VersionOptions.Published).Returns("cache-key-def");
        _cacheManager.CreatePipelineVersionCacheKey(DefaultVersionId).Returns("cache-key-graph");
        _decoratedService.FindPipelineDefinitionAsync(DefaultDefinitionId, VersionOptions.Published, Arg.Any<CancellationToken>()).Returns(definition);
        SetupDefinitionAndGraphMaterialization(definition, pipelineGraph);
        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync(DefaultDefinitionId, VersionOptions.Published);

        // Assert
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Same(pipelineGraph, result.PipelineGraph);
        _cacheManager.Received(1).CreatePipelineDefinitionVersionCacheKey(DefaultDefinitionId, VersionOptions.Published);
        _cacheManager.Received(1).CreatePipelineVersionCacheKey(DefaultVersionId);
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByDefinitionVersionId_ResolvesDefinitionAndGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        definition.Id = DefaultVersionId;
        var (_, pipelineGraph) = CreatePipelineAndGraph(DefaultDefinitionId);

        _cacheManager.CreatePipelineDefinitionVersionCacheKey(DefaultVersionId).Returns("cache-key-def");
        _cacheManager.CreatePipelineVersionCacheKey(DefaultVersionId).Returns("cache-key-graph");
        _decoratedService.FindPipelineDefinitionAsync(DefaultVersionId, Arg.Any<CancellationToken>()).Returns(definition);
        SetupDefinitionAndGraphMaterialization(definition, pipelineGraph);
        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync(DefaultVersionId);

        // Assert
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Same(pipelineGraph, result.PipelineGraph);
        _cacheManager.Received(1).CreatePipelineDefinitionVersionCacheKey(DefaultVersionId);
        _cacheManager.Received(1).CreatePipelineVersionCacheKey(DefaultVersionId);
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByHandle_ConvertsTFilterAndResolvesDefinitionAndGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        definition.Id = DefaultVersionId;
        var (_, pipelineGraph) = CreatePipelineAndGraph(DefaultDefinitionId);
        var handle = PipelineDefinitionHandle.ByDefinitionId(DefaultDefinitionId, VersionOptions.Latest);

        _cacheManager.CreatePipelineDefinitionFilterCacheKey(Arg.Any<PipelineDefinitionFilter>()).Returns("cache-key-def");
        _cacheManager.CreatePipelineVersionCacheKey(DefaultVersionId).Returns("cache-key-graph");
        _decoratedService.FindPipelineDefinitionAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>()).Returns(definition);
        SetupDefinitionAndGraphMaterialization(definition, pipelineGraph);
        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync(handle);

        // Assert
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Same(pipelineGraph, result.PipelineGraph);
        await _decoratedService.Received(1).FindPipelineDefinitionAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryFindPipelineGraphAsync_ByFilter_ResolvesDefinitionAndGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        definition.Id = DefaultVersionId;
        var (_, pipelineGraph) = CreatePipelineAndGraph(DefaultDefinitionId);
        var filter = new PipelineDefinitionFilter { DefinitionId = DefaultDefinitionId };

        _cacheManager.CreatePipelineDefinitionFilterCacheKey(filter).Returns("cache-key-def");
        _cacheManager.CreatePipelineVersionCacheKey(DefaultVersionId).Returns("cache-key-graph");
        _decoratedService.FindPipelineDefinitionAsync(filter, Arg.Any<CancellationToken>()).Returns(definition);
        SetupDefinitionAndGraphMaterialization(definition, pipelineGraph);
        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphAsync(filter);

        // Assert
        Assert.Same(definition, result.PipelineDefinition);
        Assert.Same(pipelineGraph, result.PipelineGraph);
        _cacheManager.Received(1).CreatePipelineDefinitionFilterCacheKey(filter);
        _cacheManager.Received(1).CreatePipelineVersionCacheKey(DefaultVersionId);
    }

    [Fact]
    public async Task TryFindPipelineGraphsAsync_WithAvailableMaterializer_CachesGraph()
    {
        // Arrange
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, DefaultMaterializerName);
        definition.Id = "id-1";
        var (_, graph) = CreatePipelineAndGraph(DefaultDefinitionId);
        var filter = new PipelineDefinitionFilter();

        _pipelineDefinitionStore.FindManyAsync(filter, Arg.Any<CancellationToken>()).Returns(new[] { definition });
        _cacheManager.CreatePipelineVersionCacheKey("id-1").Returns("cache-key");
        SetupDefinitionAndGraphMaterialization(definition, graph);
        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphsAsync(filter);

        // Assert
        var results = result.ToList();
        Assert.Single(results);
        Assert.Same(definition, results[0].PipelineDefinition);
        Assert.Same(graph, results[0].PipelineGraph);
    }

    [Fact]
    public async Task TryFindPipelineGraphsAsync_WithUnavailableMaterializer_ReturnsNullGraph()
    {
        // Arrange
        const string unavailableMaterializer = "unavailable-materializer";
        var definition = TestHelpers.CreatePipelineDefinition(DefaultDefinitionId, unavailableMaterializer);
        definition.Id = "id-1";
        var filter = new PipelineDefinitionFilter();

        _pipelineDefinitionStore.FindManyAsync(filter, Arg.Any<CancellationToken>()).Returns(new[] { definition });
        _cacheManager.CreatePipelineVersionCacheKey("id-1").Returns("cache-key");
        _materializerRegistry.IsMaterializerAvailable(unavailableMaterializer).Returns(false);
        var service = CreateService();

        // Act
        var result = await service.TryFindPipelineGraphsAsync(filter);

        // Assert
        var results = result.ToList();
        Assert.Single(results);
        Assert.Same(definition, results[0].PipelineDefinition);
        Assert.Null(results[0].PipelineGraph);
        await _decoratedService.DidNotReceive().MaterializePipelineAsync(Arg.Any<PipelineDefinition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryFindPipelineGraphsAsync_WithMixedMaterializers_HandlesEachCorrectly()
    {
        // Arrange
        var definition1 = TestHelpers.CreatePipelineDefinition("def-1", "available-materializer");
        definition1.Id = "id-1";
        var definition2 = TestHelpers.CreatePipelineDefinition("def-2", "unavailable-materializer");
        definition2.Id = "id-2";
        var definitions = new[] { definition1, definition2 };

        var (_, graph1) = CreatePipelineAndGraph("def-1");

        var filter = new PipelineDefinitionFilter();
        _pipelineDefinitionStore.FindManyAsync(filter, Arg.Any<CancellationToken>()).Returns(definitions);

        _cacheManager.CreatePipelineVersionCacheKey("id-1").Returns("cache-key-1");
        _cacheManager.CreatePipelineVersionCacheKey("id-2").Returns("cache-key-2");
        _materializerRegistry.IsMaterializerAvailable("available-materializer").Returns(true);
        _materializerRegistry.IsMaterializerAvailable("unavailable-materializer").Returns(false);
        _decoratedService.MaterializePipelineAsync(definition1, Arg.Any<CancellationToken>()).Returns(graph1);

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

    private CachingPipelineDefinitionService CreateService()
    {
        return new(
            _decoratedService,
            _cacheManager,
            _pipelineDefinitionStore,
            _materializerRegistry);
    }

    /// <summary>
    /// Sets up the cache manager with proper mock behavior for all cache operations.
    /// </summary>
    private void SetupCacheManager()
    {
        _cacheManager.Cache.Returns(_cache);

        var cachingOptions = Microsoft.Extensions.Options.Options.Create(new CachingOptions { CacheDuration = TimeSpan.FromMinutes(10) });
        _cache.CachingOptions.Returns(cachingOptions);

        // Setup cache to call factory functions for all types
        _cache.GetOrCreateAsync<PipelineDefinition>(Arg.Any<object>(), Arg.Any<Func<ICacheEntry, Task<PipelineDefinition>>>())
            .Returns(async callInfo => await callInfo.Arg<Func<ICacheEntry, Task<PipelineDefinition>>>()(Substitute.For<ICacheEntry>()));

        _cache.GetOrCreateAsync<PipelineGraph>(Arg.Any<object>(), Arg.Any<Func<ICacheEntry, Task<PipelineGraph>>>())
            .Returns(async callInfo => await callInfo.Arg<Func<ICacheEntry, Task<PipelineGraph>>>()(Substitute.For<ICacheEntry>()));

        _cache.GetOrCreateAsync<PipelineGraphFindResult>(Arg.Any<object>(), Arg.Any<Func<ICacheEntry, Task<PipelineGraphFindResult>>>())
            .Returns(async callInfo => await callInfo.Arg<Func<ICacheEntry, Task<PipelineGraphFindResult>>>()(Substitute.For<ICacheEntry>()));

        _cache.FindOrCreateAsync<PipelineDefinition>(Arg.Any<object>(), Arg.Any<Func<ICacheEntry, Task<PipelineDefinition>>>())
            .Returns(async callInfo => await callInfo.Arg<Func<ICacheEntry, Task<PipelineDefinition>>>()(Substitute.For<ICacheEntry>()));

        _cache.FindOrCreateAsync<PipelineGraph>(Arg.Any<object>(), Arg.Any<Func<ICacheEntry, Task<PipelineGraph>>>())
            .Returns(async callInfo => await callInfo.Arg<Func<ICacheEntry, Task<PipelineGraph>>>()(Substitute.For<ICacheEntry>()));
    }

    /// <summary>
    /// Creates a pipeline and pipeline graph for testing.
    /// </summary>
    private (Pipeline, PipelineGraph) CreatePipelineAndGraph(string definitionId = "def-1", int version = 1)
    {
        var pipeline = new Pipeline { Identity = new(definitionId, version, $"v{version}") };
        var pipelineGraph = TestHelpers.CreatePipelineGraph(pipeline);
        return (pipeline, pipelineGraph);
    }

    /// <summary>
    /// Sets up a complete pipeline definition with graph materialization for testing scenarios
    /// where definition needs to be resolved and materialized into a graph.
    /// </summary>
    private void SetupDefinitionAndGraphMaterialization(
        PipelineDefinition definition,
        PipelineGraph pipelineGraph,
        string? materializerName = null)
    {
        materializerName ??= definition.MaterializerName;
        _materializerRegistry.IsMaterializerAvailable(materializerName).Returns(true);
        _decoratedService.MaterializePipelineAsync(definition, Arg.Any<CancellationToken>()).Returns(pipelineGraph);
    }
}
