#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.Datasets;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Models;
using Stomme.Modules.Datasets.Application;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Stomme.Tests.Connectivity;
using Stomme.Tests.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The HARNESS SWAP for the predecessor platform's Datasets suites (<c>AddInMemoryPersistence</c> + <c>AddMediator</c> + <c>AddDatasetsServices</c>):
    /// the module's own <c>ServerStartup</c> on the in-process FILE store, the mediator with the host's behavior order, an
    /// allow-all guard, a settable current user, the REAL classifier over the lifted scanner unless a test hands in its
    /// own, and the request's site as a scoped <see cref="SiteState"/> (null when a test asks for "no site").
    /// </summary>
    internal sealed class DatasetsApplicationHarness : IAsyncDisposable
    {
        public const int SiteId = 1;

        public static readonly DateTime Start = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private readonly ServiceProvider _services;

        private DatasetsApplicationHarness(string root, ServiceProvider services, ManualTimeProvider clock)
        {
            _root = root;
            _services = services;
            Clock = clock;
        }

        public ManualTimeProvider Clock { get; }

        public static Task<DatasetsApplicationHarness> StartAsync(Action<IServiceCollection>? configure = null, bool withSite = true, IDataClassifier? classifier = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"stomme-datasets-app-{Guid.NewGuid():N}");
            var clock = new ManualTimeProvider(Start);
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = root,
                    ["Mcp:Server:Enabled"] = "true", // the contributor is registered only while the server half is composed
                })
                .Build());
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped(_ => new SiteState { Alias = withSite ? new Alias { AliasId = 1, TenantId = 1, SiteId = SiteId, Name = "localhost" } : null });

            new Stomme.Modules.Datasets.Startup.ServerStartup().ConfigureServices(services);

            if (classifier is not null)
            {
                services.RemoveAll<IDataClassifier>();
                services.AddSingleton(classifier);
            }

            services.RemoveAll<ICurrentUser>();
            services.AddSingleton<ICurrentUser>(new TestCurrentUser { UserId = 7, SiteId = SiteId });
            services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
            services.AddMediatorBehavior(typeof(AuthorizationBehavior<,>));
            services.AddMediatorBehavior(typeof(ValidationBehavior<,>));
            services.AddScoped<IAccessGuard, AllowAllAccessGuard>();
            configure?.Invoke(services);

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return Task.FromResult(new DatasetsApplicationHarness(root, provider, clock));
        }

        public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
        {
            await using var scope = _services.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }

        public Task<T> SendAsync<T>(IRequest<T> request) => InScopeAsync(sp => sp.GetRequiredService<IMediator>().Send(request));

        public Task<Guid> RegisterAsync(DatasetRegistration registration) => InScopeAsync(sp => sp.GetRequiredService<IDatasetRegistrar>().RegisterOrRefreshAsync(registration));

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

    /// <summary>A classifier whose answer the test sets.</summary>
    internal sealed class SettableClassifier(DataClassification result) : IDataClassifier
    {
        public DataClassification Result { get; set; } = result;

        public DataClassification Classify(IReadOnlyCollection<string> columnNames, string? sampleJson) => Result;
    }

    /// <summary>A tenant context that resolves nothing - the "no default-tenant fallback" case.</summary>
    internal sealed class NoTenantContext : ITenantContext
    {
        public TenantId? CurrentTenantId => null;

        public bool IsSystemScope => false;
    }

    internal sealed class FakeDatasetUsage(params string[] references) : IDatasetUsageProvider
    {
        public Task<IReadOnlyList<string>> FindReferencesAsync(Guid datasetId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(references);
    }
}
