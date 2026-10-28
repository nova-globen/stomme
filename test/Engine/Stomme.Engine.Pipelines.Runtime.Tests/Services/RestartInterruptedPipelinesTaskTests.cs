using Stomme.Engine.Common;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Services;

public class RestartInterruptedPipelinesTaskTests
{
    [Fact(DisplayName = "ExecuteAsync restarts each pipeline within its tenant context")]
    public async Task ExecuteAsync_RestartsWithinPipelineTenantContext()
    {
        var pipelineInstanceStore = Substitute.For<IPipelineInstanceStore>();
        var tenantAccessor = new DefaultTenantAccessor();
        var tenantService = Substitute.For<ITenantService>();
        var pipelineRestarter = Substitute.For<IPipelineRestarter>();
        var clock = Substitute.For<ISystemClock>();
        var logger = Substitute.For<ILogger<RestartInterruptedPipelinesTask>>();
        var now = DateTimeOffset.Parse("2026-04-14T12:00:00Z");
        var pipelineInstances = new List<PipelineInstanceSummary>
        {
            CreatePipelineInstance("pipeline-1", "tenant-a", now),
            CreatePipelineInstance("pipeline-2", "tenant-b", now)
        };
        var observedTenantIds = new List<string?>();

        clock.UtcNow.Returns(now);
        tenantService.FindAsync("tenant-a", Arg.Any<CancellationToken>()).Returns(new Tenant { Id = "tenant-a", Name = "Tenant A" });
        tenantService.FindAsync("tenant-b", Arg.Any<CancellationToken>()).Returns(new Tenant { Id = "tenant-b", Name = "Tenant B" });
        pipelineInstanceStore
            .SummarizeManyAsync(Arg.Any<PipelineInstanceFilter>(), Arg.Any<PageArgs>(), Arg.Any<CancellationToken>())
            .Returns(
                callInfo =>
                {
                    var pageArgs = callInfo.ArgAt<PageArgs>(1);
                    var items = pageArgs.Offset == 0 ? pipelineInstances : new List<PipelineInstanceSummary>();
                    return new ValueTask<Page<PipelineInstanceSummary>>(Page.Of(items, pipelineInstances.Count));
                });
        pipelineRestarter
            .When(x => x.RestartPipelineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => observedTenantIds.Add(tenantAccessor.Tenant?.Id));

        var options = Microsoft.Extensions.Options.Options.Create(new RuntimeOptions
        {
            RestartInterruptedPipelinesBatchSize = 10,
            InactivityThreshold = TimeSpan.FromMinutes(5)
        });
        var task = new RestartInterruptedPipelinesTask(pipelineRestarter, pipelineInstanceStore, logger, options, clock, tenantService, tenantAccessor);

        await task.ExecuteAsync(CancellationToken.None);

        Assert.Equal(new[] { "tenant-a", "tenant-b" }, observedTenantIds);
        Assert.Null(tenantAccessor.Tenant);
        await pipelineRestarter.Received(1).RestartPipelineAsync("pipeline-1", Arg.Any<CancellationToken>());
        await pipelineRestarter.Received(1).RestartPipelineAsync("pipeline-2", Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "ExecuteAsync does not push tenant context for default or agnostic tenant instances")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public async Task ExecuteAsync_DefaultOrAgnosticTenant_DoesNotPushContext(string? tenantId)
    {
        var pipelineInstanceStore = Substitute.For<IPipelineInstanceStore>();
        var tenantAccessor = new DefaultTenantAccessor();
        var tenantService = Substitute.For<ITenantService>();
        var pipelineRestarter = Substitute.For<IPipelineRestarter>();
        var clock = Substitute.For<ISystemClock>();
        var logger = Substitute.For<ILogger<RestartInterruptedPipelinesTask>>();
        var now = DateTimeOffset.Parse("2026-04-14T12:00:00Z");
        var pipelineInstances = new List<PipelineInstanceSummary>
        {
            CreatePipelineInstance("pipeline-1", tenantId, now)
        };
        var observedTenantIds = new List<string?>();

        clock.UtcNow.Returns(now);
        pipelineInstanceStore
            .SummarizeManyAsync(Arg.Any<PipelineInstanceFilter>(), Arg.Any<PageArgs>(), Arg.Any<CancellationToken>())
            .Returns(
                callInfo =>
                {
                    var pageArgs = callInfo.ArgAt<PageArgs>(1);
                    var items = pageArgs.Offset == 0 ? pipelineInstances : new List<PipelineInstanceSummary>();
                    return new ValueTask<Page<PipelineInstanceSummary>>(Page.Of(items, pipelineInstances.Count));
                });
        pipelineRestarter
            .When(x => x.RestartPipelineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => observedTenantIds.Add(tenantAccessor.Tenant?.Id));

        var options = Microsoft.Extensions.Options.Options.Create(new RuntimeOptions
        {
            RestartInterruptedPipelinesBatchSize = 10,
            InactivityThreshold = TimeSpan.FromMinutes(5)
        });
        var task = new RestartInterruptedPipelinesTask(pipelineRestarter, pipelineInstanceStore, logger, options, clock, tenantService, tenantAccessor);

        await task.ExecuteAsync(CancellationToken.None);

        Assert.Equal(new string?[] { null }, observedTenantIds);
        Assert.Null(tenantAccessor.Tenant);
        await tenantService.DidNotReceive().FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await pipelineRestarter.Received(1).RestartPipelineAsync("pipeline-1", Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ExecuteAsync continues restarting later pipelines after a failure")]
    public async Task ExecuteAsync_ContinuesAfterFailure()
    {
        var pipelineInstanceStore = Substitute.For<IPipelineInstanceStore>();
        var tenantAccessor = new DefaultTenantAccessor();
        var tenantService = Substitute.For<ITenantService>();
        var pipelineRestarter = Substitute.For<IPipelineRestarter>();
        var clock = Substitute.For<ISystemClock>();
        var logger = Substitute.For<ILogger<RestartInterruptedPipelinesTask>>();
        var now = DateTimeOffset.Parse("2026-04-14T12:00:00Z");
        var pipelineInstances = new List<PipelineInstanceSummary>
        {
            CreatePipelineInstance("pipeline-1", "tenant-a", now),
            CreatePipelineInstance("pipeline-2", "tenant-b", now)
        };

        clock.UtcNow.Returns(now);
        tenantService.FindAsync("tenant-a", Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<Tenant?>(new InvalidOperationException("Transient tenant lookup failure")));
        tenantService.FindAsync("tenant-b", Arg.Any<CancellationToken>()).Returns(new Tenant { Id = "tenant-b", Name = "Tenant B" });
        pipelineInstanceStore
            .SummarizeManyAsync(Arg.Any<PipelineInstanceFilter>(), Arg.Any<PageArgs>(), Arg.Any<CancellationToken>())
            .Returns(
                callInfo =>
                {
                    var pageArgs = callInfo.ArgAt<PageArgs>(1);
                    var items = pageArgs.Offset == 0 ? pipelineInstances : new List<PipelineInstanceSummary>();
                    return new ValueTask<Page<PipelineInstanceSummary>>(Page.Of(items, pipelineInstances.Count));
                });

        var options = Microsoft.Extensions.Options.Options.Create(new RuntimeOptions
        {
            RestartInterruptedPipelinesBatchSize = 10,
            InactivityThreshold = TimeSpan.FromMinutes(5)
        });
        var task = new RestartInterruptedPipelinesTask(pipelineRestarter, pipelineInstanceStore, logger, options, clock, tenantService, tenantAccessor);

        await task.ExecuteAsync(CancellationToken.None);

        await pipelineRestarter.DidNotReceive().RestartPipelineAsync("pipeline-1", Arg.Any<CancellationToken>());
        await pipelineRestarter.Received(1).RestartPipelineAsync("pipeline-2", Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ExecuteAsync restarts tenant-specific pipelines when tenant services are unavailable")]
    public async Task ExecuteAsync_WithoutTenantServices_RestartsPipeline()
    {
        var pipelineInstanceStore = Substitute.For<IPipelineInstanceStore>();
        var pipelineRestarter = Substitute.For<IPipelineRestarter>();
        var clock = Substitute.For<ISystemClock>();
        var logger = Substitute.For<ILogger<RestartInterruptedPipelinesTask>>();
        var now = DateTimeOffset.Parse("2026-04-14T12:00:00Z");
        var pipelineInstances = new List<PipelineInstanceSummary>
        {
            CreatePipelineInstance("pipeline-1", "tenant-a", now)
        };

        clock.UtcNow.Returns(now);
        pipelineInstanceStore
            .SummarizeManyAsync(Arg.Any<PipelineInstanceFilter>(), Arg.Any<PageArgs>(), Arg.Any<CancellationToken>())
            .Returns(
                callInfo =>
                {
                    var pageArgs = callInfo.ArgAt<PageArgs>(1);
                    var items = pageArgs.Offset == 0 ? pipelineInstances : new List<PipelineInstanceSummary>();
                    return new ValueTask<Page<PipelineInstanceSummary>>(Page.Of(items, pipelineInstances.Count));
                });

        var options = Microsoft.Extensions.Options.Options.Create(new RuntimeOptions
        {
            RestartInterruptedPipelinesBatchSize = 10,
            InactivityThreshold = TimeSpan.FromMinutes(5)
        });
        var task = new RestartInterruptedPipelinesTask(pipelineRestarter, pipelineInstanceStore, logger, options, clock);

        await task.ExecuteAsync(CancellationToken.None);

        await pipelineRestarter.Received(1).RestartPipelineAsync("pipeline-1", Arg.Any<CancellationToken>());
    }

    private static PipelineInstanceSummary CreatePipelineInstance(string id, string? tenantId, DateTimeOffset updatedAt)
    {
        return new PipelineInstanceSummary
        {
            Id = id,
            TenantId = tenantId,
            DefinitionId = "definition",
            DefinitionVersionId = "definition:1",
            Status = PipelineStatus.Running,
            SubStatus = PipelineSubStatus.Pending,
            CreatedAt = updatedAt.AddMinutes(-10),
            UpdatedAt = updatedAt.AddMinutes(-10)
        };
    }
}