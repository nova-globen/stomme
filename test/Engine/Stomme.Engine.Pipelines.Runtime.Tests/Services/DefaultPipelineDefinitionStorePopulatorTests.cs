using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.Logging;
using Open.Linq.AsyncExtensions;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Services;

public class DefaultPipelineDefinitionStorePopulatorTests
{
    private readonly IPipelineDefinitionStore _storeMock;
    private readonly DefaultPipelineDefinitionStorePopulator _populator;
    private readonly List<PipelineDefinition> _pipelineDefinitionsInStore = new();

    public DefaultPipelineDefinitionStorePopulatorTests()
    {
        _storeMock = Substitute.For<IPipelineDefinitionStore>();
        _storeMock.FindManyAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(_pipelineDefinitionsInStore);
        _populator = new(() => new List<IPipelinesProvider>(),
            Substitute.For<ITriggerIndexer>(),
            _storeMock,
            Substitute.For<IStepSerializer>(),
            Substitute.For<IPayloadSerializer>(),
            Substitute.For<ISystemClock>(),
            Substitute.For<IIdentityGraphService>(),
            Substitute.For<ITenantAccessor>(),
            Substitute.For<ILogger<DefaultPipelineDefinitionStorePopulator>>());
    }

    [Fact(DisplayName = "When adding a new pipeline it needs to be saved")]
    public async Task AddOrUpdateCoreAsync_NewPipelineDefinition_AddsPipelineDefinition()
    {
        var pipeline = new MaterializedPipeline(new()
        {
            Identity = new("a", 7, "1"),
            Publication = new(true, true)
        }, "Test", "Test");

        await _populator.AddAsync(pipeline);
        
        await CheckAmountOfPipelinesSaved(1);
        await CheckIfPipelineWasSaved(pipeline.Pipeline.Identity, wd => wd.IsLatest);
    }

    [Fact(DisplayName = "When adding a pipeline with the same version, ignore it")]
    public async Task AddOrUpdateCoreAsync_ExistingPipelineDefinition_KeepsExistingPipeline()
    {
        _pipelineDefinitionsInStore.Add(new()
        {
            Id = "1",
            DefinitionId = "a",
            Version = 1,
            Inputs = new List<InputDefinition>
            {
                new()
                {
                    Name = "InputA"
                }
            }
        });

        var pipeline = new MaterializedPipeline(new()
        {
            Identity = new("a", 1, "1"),
            Inputs = new List<InputDefinition>
            {
                new()
                {
                    Name = "A"
                }
            }
        }, "Test", "Test");

        await _populator.AddAsync(pipeline);
        
        await _storeMock.DidNotReceive().SaveManyAsync(Arg.Any<IEnumerable<PipelineDefinition>>(), Arg.Any<CancellationToken>());
    }

    
    [Theory(DisplayName = "When adding a newer pipeline version, add it as latest and update the publication settings of older pipelines")]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    public async Task AddOrUpdateCoreAsync_NewPipelineDefinitionVersion_AddsPipelineDefinition(bool pipelineAddedIsLatest, bool pipelineAddedIsPublished, bool expectedPublishedStateAdded, bool expectedPublishedStateExisting)
    {
        _pipelineDefinitionsInStore.Add(new()
        {
            Id = "1",
            DefinitionId = "a",
            Version = 1,
            IsLatest = true,
            IsPublished = true
        });
        var pipeline = new MaterializedPipeline(new()
        {
            Identity = new("a", 2, "2"),
            Publication = new(pipelineAddedIsLatest, pipelineAddedIsPublished)
        }, "Test", "Test");

        await _populator.AddAsync(pipeline);
        
        await CheckIfPipelineWasSaved(wd => wd is { Id: "1", IsLatest: false } 
                                            && wd.IsPublished == expectedPublishedStateExisting);
        await CheckIfPipelineWasSaved(pipeline.Pipeline.Identity, wd => wd.IsLatest && wd.IsPublished == expectedPublishedStateAdded);
    }

    [Fact(DisplayName = "When adding a newer pipeline version, ignore the root Version number")]
    public async Task AddOrUpdateCoreAsync_NewPipelineDefinitionVersion_IgnoreRootVersion()
    {
        _pipelineDefinitionsInStore.Add(new()
        {
            Id = "2",
            DefinitionId = "a",
            Version = 2,
            IsLatest = true,
            IsPublished = true
        });

        var pipeline = new MaterializedPipeline(new()
        {
            Identity = new("a", 3, "1"),
            Version = 1,
            Publication = new(true, true)
        }, "Test", "Test");

        await _populator.AddAsync(pipeline);

        await CheckIfPipelineWasSaved(wd => wd is { Id: "2", IsLatest: false, IsPublished: false });
        await CheckIfPipelineWasSaved(pipeline.Pipeline.Identity, wd => wd is { IsLatest: true, IsPublished: true });
    }

    [Fact(DisplayName = "When adding a pipeline version with the same ID, ignore it")]
    public async Task AddOrUpdateCoreAsync_NewPipelineDefinitionVersionWithSameId_IsIgnored()
    {
        _pipelineDefinitionsInStore.Add(
            new()
            {
                Id = "1",
                DefinitionId = "a",
                Version = 1,
            });

        var pipeline = new MaterializedPipeline(new()
        {
            Identity = new("a", 2, "1")
        }, "Test", "Test");

        await _populator.AddAsync(pipeline);
        
        await _storeMock.DidNotReceive()
            .SaveManyAsync(Arg.Any<IEnumerable<PipelineDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "When adding a older pipeline version, keep the current latest as latest")]
    public async Task AddOrUpdateCoreAsync_OlderVersionAsLatest_ExistingVersionShouldRemainLatest()
    {
        _pipelineDefinitionsInStore.Add(
            new()
            {
                Id = "2",
                DefinitionId = "a",
                Version = 2,
                IsLatest = true
            });
        
        var pipeline = new MaterializedPipeline(new()
        {
            Identity = new("a", 1, "1"),
            Publication = new(true, true)
        }, "Test", "Test");

        await _populator.AddAsync(pipeline);
        
        await _storeMock.DidNotReceive().SaveManyAsync(Arg.Is<IEnumerable<PipelineDefinition>>(l =>
            l.Any(wd => wd.Id == "2")), Arg.Any<CancellationToken>());

        await CheckIfPipelineWasSaved(pipeline.Pipeline.Identity, wd => wd.IsLatest == false);
    }

    [Fact(DisplayName = "When adding a newer pipeline version, keep the current published version published")]
    public async Task AddOrUpdateCoreAsync_OlderVersionAsPublished_ShouldNotBeUpdated()
    {
        _storeMock.FindManyAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<PipelineDefinition>
            {
                new()
                {
                    Id = "2",
                    DefinitionId = "a",
                    Version = 2,
                    IsPublished = true
                }
            });

        var pipeline = new MaterializedPipeline(new()
        {
            Identity = new("a", 1, "1"),
            Publication = new(true, true)
        }, "Test", "Test");

        await _populator.AddAsync(pipeline);
        await _storeMock.DidNotReceive().SaveManyAsync(Arg.Is<IEnumerable<PipelineDefinition>>(l =>
            l.Any(wd => wd.Id == "2")), Arg.Any<CancellationToken>());
        await _storeMock.Received().SaveManyAsync(Arg.Is<IEnumerable<PipelineDefinition>>(l =>
            l.Any(wd => wd.Id == "1" && wd.IsPublished == false)), Arg.Any<CancellationToken>());
    }

    private async Task CheckIfPipelineWasSaved(PipelineIdentity identity, Func<PipelineDefinition, bool> predicate)
    {
        await _storeMock.Received(1).SaveManyAsync(Arg.Is<IEnumerable<PipelineDefinition>>(l =>
            l.Where(wd => wd.Id == identity.Id
                          && wd.DefinitionId == identity.DefinitionId
                          && wd.Version == identity.Version).Any(predicate)), Arg.Any<CancellationToken>());
    }

    private async Task CheckIfPipelineWasSaved(Func<PipelineDefinition, bool> predicate)
    {
        await _storeMock.Received(1).SaveManyAsync(Arg.Is<IEnumerable<PipelineDefinition>>(l =>
            l.Any(predicate)), Arg.Any<CancellationToken>());
    }

    private async Task CheckAmountOfPipelinesSaved(int count)
    {
        await _storeMock.Received(count)
            .SaveManyAsync(Arg.Any<IEnumerable<PipelineDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "PopulateStoreAsync imports pipelines from current tenant")]
    public async Task PopulateStoreAsync_CurrentTenantPipelines_ImportsPipelines()
    {
        var currentTenantId = "tenant-1";

        var pipeline1 = CreateMaterializedPipeline("pipeline-1", "id-1", currentTenantId);
        var pipeline2 = CreateMaterializedPipeline("pipeline-2", "id-2", currentTenantId);

        var populator = CreatePopulatorWithTenant(currentTenantId, pipeline1, pipeline2);
        var result = await populator.PopulateStoreAsync();

        Assert.Equal(2, result.Count());
        await _storeMock.Received(2).SaveManyAsync(Arg.Any<IEnumerable<PipelineDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "PopulateStoreAsync skips pipelines from other tenants")]
    public async Task PopulateStoreAsync_OtherTenantPipelines_SkipsPipelines()
    {
        var currentTenantId = "tenant-1";
        var otherTenantId = "tenant-2";

        var pipelineCurrentTenant = CreateMaterializedPipeline("pipeline-1", "id-1", currentTenantId);
        var pipelineOtherTenant = CreateMaterializedPipeline("pipeline-2", "id-2", otherTenantId);

        var populator = CreatePopulatorWithTenant(currentTenantId, pipelineCurrentTenant, pipelineOtherTenant);
        var result = await populator.PopulateStoreAsync().ToList();

        Assert.Single(result);
        Assert.Equal("pipeline-1", result.First().DefinitionId);
        await _storeMock.Received(1).SaveManyAsync(Arg.Any<IEnumerable<PipelineDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "PopulateStoreAsync handles null/empty tenant IDs correctly")]
    [InlineData(null, null, true)]  // Both null - should import
    [InlineData("", "", true)]      // Both empty - should import
    [InlineData(null, "", true)]    // Normalized as same - should import
    [InlineData("tenant-1", null, false)]  // Different tenants - should skip
    [InlineData("tenant-1", "", false)]    // Different tenants - should skip
    public async Task PopulateStoreAsync_NullOrEmptyTenantIds_HandlesCorrectly(string? currentTenantId, string? pipelineTenantId, bool shouldImport)
    {
        var pipeline = CreateMaterializedPipeline("pipeline-1", "id-1", pipelineTenantId);
        var populator = CreatePopulatorWithTenant(currentTenantId, pipeline);
        var result = await populator.PopulateStoreAsync();

        if (shouldImport)
        {
            Assert.Single(result);
            await _storeMock.Received(1).SaveManyAsync(Arg.Any<IEnumerable<PipelineDefinition>>(), Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.Empty(result);
            await _storeMock.DidNotReceive().SaveManyAsync(Arg.Any<IEnumerable<PipelineDefinition>>(), Arg.Any<CancellationToken>());
        }
    }

    private MaterializedPipeline CreateMaterializedPipeline(string definitionId, string id, string? tenantId)
    {
        return new(new()
        {
            Identity = new(definitionId, 1, id, tenantId),
            Publication = new(true, true)
        }, "Test", "TestProvider");
    }

    private DefaultPipelineDefinitionStorePopulator CreatePopulatorWithTenant(string? tenantId, params MaterializedPipeline[] pipelines)
    {
        var tenantAccessor = Substitute.For<ITenantAccessor>();
        tenantAccessor.Tenant.Returns(tenantId != null ? new Tenant { Id = tenantId } : null);
        tenantAccessor.TenantId.Returns(tenantId.NormalizeTenantId());

        var provider = Substitute.For<IPipelinesProvider>();
        provider.Name.Returns("TestProvider");
        provider.GetPipelinesAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IEnumerable<MaterializedPipeline>>(pipelines));

        return new(
            () => new List<IPipelinesProvider> { provider },
            Substitute.For<ITriggerIndexer>(),
            _storeMock,
            Substitute.For<IStepSerializer>(),
            Substitute.For<IPayloadSerializer>(),
            Substitute.For<ISystemClock>(),
            Substitute.For<IIdentityGraphService>(),
            tenantAccessor,
            Substitute.For<ILogger<DefaultPipelineDefinitionStorePopulator>>());
    }
}