#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.Datasets;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Models;
using Stomme.Modules.Dashboards.Application;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tests.Connectivity;
using Stomme.Tests.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// The Dashboards application layer over the File store through the module's OWN startup: the
    /// mediator with the authorization and validation behaviours, a resolved site, a stub Datasets catalogue whose
    /// descriptor and rows a test sets - the shape the earlier tests reached through in-memory persistence and a stub
    /// catalog, on the composition the host uses.
    /// </summary>
    internal sealed class DashboardsApplicationHarness : IAsyncDisposable
    {
        public const int SiteId = 1;

        public static readonly DateTime Start = new(2026, 9, 27, 13, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private readonly ServiceProvider _services;

        private DashboardsApplicationHarness(string root, ServiceProvider services, ManualTimeProvider clock, StubDatasetCatalog catalog)
        {
            _root = root;
            _services = services;
            Clock = clock;
            Catalog = catalog;
        }

        public ManualTimeProvider Clock { get; }

        public StubDatasetCatalog Catalog { get; }

        public static Task<DashboardsApplicationHarness> StartAsync(StubDatasetCatalog? catalog = null, bool withSite = true, Action<IServiceCollection>? configure = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"stomme-dashboards-app-{Guid.NewGuid():N}");
            var clock = new ManualTimeProvider(Start);
            catalog ??= new StubDatasetCatalog();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = root,
                    ["Mcp:Server:Enabled"] = "true", // the MCP contributor is registered only while the server half is composed
                })
                .Build());
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped(_ => new SiteState { Alias = withSite ? new Alias { AliasId = 1, TenantId = 1, SiteId = SiteId, Name = "localhost" } : null });
            services.AddSingleton<IDatasetCatalog>(catalog);

            new Stomme.Modules.Dashboards.Startup.ServerStartup().ConfigureServices(services);

            services.RemoveAll<ICurrentUser>();
            services.AddSingleton<ICurrentUser>(new TestCurrentUser { UserId = 7, SiteId = SiteId });
            services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
            services.AddMediatorBehavior(typeof(AuthorizationBehavior<,>));
            services.AddMediatorBehavior(typeof(ValidationBehavior<,>));
            services.AddScoped<IAccessGuard, AllowAllAccessGuard>();
            configure?.Invoke(services);

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return Task.FromResult(new DashboardsApplicationHarness(root, provider, clock, catalog));
        }

        public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
        {
            await using var scope = _services.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }

        public Task<T> SendAsync<T>(IRequest<T> request) => InScopeAsync(sp => sp.GetRequiredService<IMediator>().Send(request));

        public Task<Guid> CreateBoardAsync(string name = "Board", int columns = 12, int rowHeightPx = 40) => SendAsync(new CreateDashboardCommand(name, columns, rowHeightPx));

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A Datasets catalogue a test sets: one descriptor per dataset id, one preview answer for all.</summary>
    internal sealed class StubDatasetCatalog : IDatasetCatalog
    {
        private readonly Dictionary<Guid, DatasetDescriptor> _descriptors = new();

        public DatasetPreviewRows Rows { get; set; } = new(true, "[]", "OK");

        /// <summary>A dataset's own preview answer, where a test needs two datasets to answer differently.</summary>
        public Dictionary<Guid, DatasetPreviewRows> RowsFor { get; } = new();

        public StubDatasetCatalog With(DatasetDescriptor descriptor)
        {
            _descriptors[descriptor.Id] = descriptor;
            return this;
        }

        public static DatasetDescriptor Descriptor(Guid id, DataClassification classification, params string[] columns) =>
            new(id, "Sales", OriginKind.PipelineOutput, "pipeline-sales", classification,
                new DatasetSchemaDescriptor(columns.Select(column => new DatasetColumnDescriptor(column, ColumnDataType.String, null)).ToList()),
                new DatasetSummaryDescriptor(columns.Select(column => new DatasetColumnSummaryDescriptor(column, Array.Empty<string>(), null, null, 0, 0)).ToList(), 0),
                DashboardsApplicationHarness.Start);

        public Task<IReadOnlyList<DatasetDescriptor>> ListDescriptorsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DatasetDescriptor>>(_descriptors.Values.ToList());

        public Task<DatasetDescriptor?> GetDescriptorAsync(Guid datasetId, CancellationToken ct = default) => Task.FromResult(_descriptors.TryGetValue(datasetId, out var descriptor) ? descriptor : null);

        public Task<DatasetPreviewRows> GetPreviewRowsAsync(Guid datasetId, CancellationToken ct = default) => Task.FromResult(RowsFor.TryGetValue(datasetId, out var rows) ? rows : Rows);
    }
}
