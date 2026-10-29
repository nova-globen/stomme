#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.CloudUsage;
using Stomme.CloudUsage.Persistence;
using Stomme.Connectivity;
using Stomme.Datasets;
using Stomme.Infrastructure.Connectivity;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules.CloudUsage.Application;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.CloudUsage.Providers;
using Stomme.Notifications;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Stomme.Tests.Connectivity;
using Stomme.Tests.Connectors;
using Stomme.Tests.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests.CloudUsage
{
    /// <summary>
    /// The CloudUsage application layer over the File store through the module's OWN startup: the
    /// mediator with the authorization and validation behaviours, a resolved site and tenant, and the edges the predecessor platform's tests
    /// stubbed - the payload source (a captured sample), the connection resolver, the dataset registrar, the notification
    /// dispatcher, the site's administrators, the host egress.
    /// </summary>
    internal sealed class CloudUsageApplicationHarness : IAsyncDisposable
    {
        public const int SiteId = 1;

        // the predecessor platform's clock: today is 2026-07-15, so the evaluated month is 2026-07 and the latest full day 2026-07-14.
        public static readonly DateTime Start = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

        public const string AzurePayload = """
            {
              "properties": {
                "columns": [
                  { "name": "PreTaxCost" }, { "name": "UsageDate" }, { "name": "ServiceName" },
                  { "name": "ResourceId" }, { "name": "Currency" }
                ],
                "rows": [
                  [ 12.34, 20260714, "Virtual Machines", "/subscriptions/s/vm1", "USD" ],
                  [ 0.56, 20260714, "Storage", "/subscriptions/s/sa1", "USD" ]
                ]
              }
            }
            """;

        private readonly string _root;
        private readonly ServiceProvider _services;

        private CloudUsageApplicationHarness(string root, ServiceProvider services, Stubs stubs)
        {
            _root = root;
            _services = services;
            Clock = stubs.Clock;
            Payload = stubs.Payload;
            Connections = stubs.Connections;
            Registrar = stubs.Registrar;
            Dispatcher = stubs.Dispatcher;
            Administrators = stubs.Administrators;
        }

        public ManualTimeProvider Clock { get; }

        public CapturingPayloadSource Payload { get; }

        public FakeConnectionResolver Connections { get; }

        public RecordingDatasetRegistrar Registrar { get; }

        public RecordingNotificationDispatcher Dispatcher { get; }

        public StubAdministrators Administrators { get; }

        public static ResolvedConnection AzureConnection(string? secret = "sp-token", string type = "azurecost") =>
            new(Guid.NewGuid(), SiteId, type, "https://management.azure.com/query", new Dictionary<string, string> { ["subscriptionId"] = "sub-123" }, secret);

        private sealed class Stubs
        {
            public ManualTimeProvider Clock { get; } = new(Start);

            public CapturingPayloadSource Payload { get; } = new(AzurePayload);

            public FakeConnectionResolver Connections { get; } = new(AzureConnection());

            public RecordingDatasetRegistrar Registrar { get; } = new();

            public RecordingNotificationDispatcher Dispatcher { get; } = new();

            public StubAdministrators Administrators { get; } = new(new[] { 5 });
        }

        public static Task<CloudUsageApplicationHarness> StartAsync(bool withSite = true, bool onlyAzure = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"stomme-cloudusage-app-{Guid.NewGuid():N}");
            var stubs = new Stubs();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = root,
                })
                .Build());
            services.AddSingleton<TimeProvider>(stubs.Clock);
            services.AddScoped(_ => new SiteState { Alias = withSite ? new Alias { AliasId = 1, TenantId = 1, SiteId = SiteId, Name = "localhost" } : null });

            new Stomme.Modules.CloudUsage.Startup.ServerStartup().ConfigureServices(services);

            services.RemoveAll<ICloudCostPayloadSource>();
            services.AddSingleton<ICloudCostPayloadSource>(stubs.Payload);
            if (onlyAzure)
            {
                services.RemoveAll<ICloudCostProviderRegistry>();
                services.AddSingleton<ICloudCostProviderRegistry>(sp => new CloudCostProviderRegistry(new ICloudCostProvider[] { new AzureCostProvider(stubs.Payload, sp.GetRequiredService<ICloudCostNormalizer>()) }));
            }

            services.RemoveAll<ICloudCostConnectionResolver>();
            services.AddSingleton<ICloudCostConnectionResolver>(stubs.Connections);
            services.AddSingleton<IDatasetRegistrar>(stubs.Registrar);
            services.AddSingleton<IConnectorEgressFactory>(new StubEgressFactory());
            services.AddSingleton<INotificationDispatcher>(stubs.Dispatcher);
            services.AddSingleton<IUserRoleRepository>(stubs.Administrators);
            services.RemoveAll<ICurrentUser>();
            services.AddSingleton<ICurrentUser>(new TestCurrentUser { UserId = 7, SiteId = SiteId });
            services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
            services.AddMediatorBehavior(typeof(AuthorizationBehavior<,>));
            services.AddMediatorBehavior(typeof(ValidationBehavior<,>));
            services.AddScoped<IAccessGuard, AllowAllAccessGuard>();

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return Task.FromResult(new CloudUsageApplicationHarness(root, provider, stubs));
        }

        public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
        {
            await using var scope = _services.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }

        public Task<T> SendAsync<T>(IRequest<T> request) => InScopeAsync(sp => sp.GetRequiredService<IMediator>().Send(request));

        public Task<IReadOnlyList<CloudCostRecordAggregate>> LedgerAsync(int siteId = SiteId) => InScopeAsync(sp => sp.GetRequiredService<ICloudCostRecordRepository>().ListForSiteAsync(siteId));

        public Task SpendAsync(DateOnly date, decimal cost, string currency = "USD", CloudProvider provider = CloudProvider.Azure, string service = "Virtual Machines",
            CloudServiceCategory category = CloudServiceCategory.Compute, int siteId = SiteId) =>
            InScopeAsync(async sp =>
            {
                await sp.GetRequiredService<ICloudCostRecordRepository>().AddAsync(CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, siteId,
                    new CloudCostEntry(date, provider, category, service, "vm-1", cost, currency)));
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return 0;
            });

        public Task BudgetAsync(decimal amount, string currency = "USD", CloudProvider? provider = null, int siteId = SiteId) =>
            InScopeAsync(async sp =>
            {
                await sp.GetRequiredService<ICloudBudgetRepository>().AddAsync(CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, siteId, amount, currency, provider));
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return 0;
            });

        public Task<int> EvaluateAlertsAsync(CloudUsageAlertOptions? options = null, int siteId = SiteId) =>
            InScopeAsync(sp => sp.GetRequiredService<CloudCostAlertService>().EvaluateAsync(TenantId.Default, siteId, options ?? new CloudUsageAlertOptions { Enabled = true }));

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

    internal sealed class CapturingPayloadSource(string payload) : ICloudCostPayloadSource
    {
        public string Payload { get; set; } = payload;

        public Exception? Throw { get; set; }

        public CloudCostQuery? LastQuery { get; private set; }

        public Task<string> ReadAsync(CloudCostQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Throw is { } failure ? Task.FromException<string>(failure) : Task.FromResult(Payload);
        }
    }

    internal sealed class FakeConnectionResolver(ResolvedConnection? connection) : ICloudCostConnectionResolver
    {
        public ResolvedConnection? Connection { get; set; } = connection;

        public Task<ResolvedConnection?> ResolveAsync(Guid connectionId, CancellationToken ct = default) =>
            Task.FromResult(Connection is null ? null : Connection with { ConnectionId = connectionId });
    }

    internal sealed class RecordingDatasetRegistrar : IDatasetRegistrar
    {
        public List<DatasetRegistration> Registrations { get; } = new();

        public Task<Guid> RegisterOrRefreshAsync(DatasetRegistration registration, CancellationToken ct = default)
        {
            Registrations.Add(registration);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    internal sealed class RecordingNotificationDispatcher : INotificationDispatcher
    {
        public List<NotificationDispatchRequest> Requests { get; } = new();

        public bool Fail { get; set; }

        public Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("dispatch refused");
            }

            Requests.Add(request);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    internal sealed class StubEgressFactory : IConnectorEgressFactory
    {
        public Task<IConnectorEgress> ForSiteAsync(int siteId, CancellationToken cancellationToken = default) => Task.FromResult<IConnectorEgress>(new StubEgress());
    }

    internal sealed class StubAdministrators : IUserRoleRepository
    {
        public StubAdministrators(int[] userIds) => UserIds = userIds;

        public int[] UserIds { get; set; }

        public Task<IEnumerable<UserRole>> GetUserRolesAsync(string roleName, int siteId) =>
            Task.FromResult<IEnumerable<UserRole>>(roleName == RoleNames.Admin ? UserIds.Select(id => new UserRole { UserId = id }).ToList() : new List<UserRole>());

        public Task<IEnumerable<UserRole>> GetUserRolesAsync(int siteId) => throw new NotSupportedException();

        public Task<IEnumerable<UserRole>> GetUserRolesAsync(int userId, int siteId) => throw new NotSupportedException();

        public Task<UserRole> AddUserRoleAsync(UserRole userRole) => throw new NotSupportedException();

        public Task<UserRole> UpdateUserRoleAsync(UserRole userRole) => throw new NotSupportedException();

        public Task<UserRole> GetUserRoleAsync(int userRoleId) => throw new NotSupportedException();

        public Task<UserRole> GetUserRoleAsync(int userRoleId, bool tracking) => throw new NotSupportedException();

        public Task<UserRole> GetUserRoleAsync(int userId, int roleId) => throw new NotSupportedException();

        public Task<UserRole> GetUserRoleAsync(int userId, int roleId, bool tracking) => throw new NotSupportedException();

        public Task DeleteUserRoleAsync(int userRoleId) => throw new NotSupportedException();

        public Task DeleteUserRolesAsync(int userId) => throw new NotSupportedException();
    }
}
