#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Models;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tests.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// The harness for the Connectivity application suites (<c>AddInMemoryPersistence</c> + <c>AddMediator</c> +
    /// a fake vault): the module's own <c>ServerStartup</c> on the in-process FILE store (a real serialize/deserialize per
    /// scope), the mediator with the host's behavior order (authorization outermost, validation innermost), a dictionary
    /// <see cref="ISiteSecretStore"/>, a settable <see cref="ICurrentUser"/>, the connectors given, the host's catalog, and
    /// ONE egress for every site (a double by default; the real <see cref="HostConnectorEgress"/> in the egress suite).
    /// The guard is ALLOW-ALL: these suites pin what the handlers DO; what the keys DECIDE is pinned over real HTTP by
    /// the module drive (ConnectionsModuleDriveTests).
    /// </summary>
    internal sealed class ConnectivityApplicationHarness : IAsyncDisposable
    {
        public const int SiteId = 1;
        public const int FrameworkTenantId = 1;

        public static readonly DateTime Start = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private readonly ServiceProvider _services;

        private ConnectivityApplicationHarness(string root, ServiceProvider services, FakeSiteSecretStore secrets, TestCurrentUser user, ConcurrentQueue<string> logs, IConnectorEgress egress)
        {
            _root = root;
            _services = services;
            Secrets = secrets;
            User = user;
            Logs = logs;
            Egress = egress;
        }

        public FakeSiteSecretStore Secrets { get; }

        public TestCurrentUser User { get; }

        public ConcurrentQueue<string> Logs { get; }

        public IConnectorEgress Egress { get; }

        public static Task<ConnectivityApplicationHarness> StartAsync(IConnector connector, Action<IServiceCollection>? configure = null, IConnectorEgress? egress = null, IReadOnlyDictionary<string, string?>? extraConfiguration = null) =>
            StartAsync(new[] { connector }, configure, egress, extraConfiguration);

        public static Task<ConnectivityApplicationHarness> StartAsync(IEnumerable<IConnector> connectors, Action<IServiceCollection>? configure = null, IConnectorEgress? egress = null, IReadOnlyDictionary<string, string?>? extraConfiguration = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"stomme-connectivity-app-{Guid.NewGuid():N}");
            var secrets = new FakeSiteSecretStore();
            var user = new TestCurrentUser { UserId = 7, SiteId = SiteId };
            var logs = new ConcurrentQueue<string>();
            var theEgress = egress ?? NoEgress.Instance;

            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(new CapturingLoggerProvider(logs));
            });
            var configuration = new Dictionary<string, string?>
            {
                ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                ["ModuleStore:FilePath"] = root,
            };
            foreach (var (key, value) in extraConfiguration ?? new Dictionary<string, string?>())
            {
                configuration[key] = value;
            }

            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
            services.AddSingleton<TimeProvider>(new ManualTimeProvider(Start));
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = FrameworkTenantId, SiteId = SiteId, Name = "localhost" } });

            new Stomme.Modules.Connectivity.Startup.ServerStartup().ConfigureServices(services);
            // The in-process harness composes no file manager, so it offers no managed file storage: the extraction context's
            // FileSink is null here, exactly as on a host without it; the sink itself is driven on an installed host.
            services.RemoveAll<IConnectorFileSink>();

            foreach (var connector in connectors)
            {
                services.AddSingleton(connector);
            }

            services.AddConnectorCatalog();
            services.AddSingleton<IConnectorEgressFactory>(new FixedEgressFactory(theEgress));
            services.RemoveAll<ISiteSecretStore>();
            services.AddSingleton<ISiteSecretStore>(secrets);
            services.RemoveAll<ICurrentUser>();
            services.AddSingleton<ICurrentUser>(user);

            services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
            services.AddMediatorBehavior(typeof(AuthorizationBehavior<,>));
            services.AddMediatorBehavior(typeof(ValidationBehavior<,>));
            services.AddScoped<IAccessGuard, AllowAllAccessGuard>();

            configure?.Invoke(services);

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return Task.FromResult(new ConnectivityApplicationHarness(root, provider, secrets, user, logs, theEgress));
        }

        public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
        {
            await using var scope = _services.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }

        public Task<T> SendAsync<T>(IRequest<T> request) => InScopeAsync(sp => sp.GetRequiredService<IMediator>().Send(request));

        public Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string>? parameters = null, int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords) =>
            InScopeAsync(sp => sp.GetRequiredService<IConnectorExtractionService>().ExtractAsync(connectionId, operation, parameters ?? new Dictionary<string, string>(), maxRecords));

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
                // a leftover temp directory is not worth failing a green run over
            }
        }

        private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

            public void Dispose()
            {
            }

            private sealed class CapturingLogger(ConcurrentQueue<string> logs) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                    logs.Enqueue($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }

    /// <summary>A current user whose roles the test sets.</summary>
    internal sealed class TestCurrentUser : ICurrentUser
    {
        public int? UserId { get; set; }

        public string? UserName => UserId is null ? null : "user" + UserId;

        public bool IsAuthenticated => UserId is not null;

        public HashSet<string> RoleSet { get; } = new(StringComparer.OrdinalIgnoreCase) { RoleNames.Admin };

        public IReadOnlySet<string> Roles => RoleSet;

        public int SiteId { get; set; }
    }

    internal sealed class FixedEgressFactory(IConnectorEgress egress) : IConnectorEgressFactory
    {
        public Task<IConnectorEgress> ForSiteAsync(int siteId, CancellationToken cancellationToken = default) => Task.FromResult(egress);
    }

    /// <summary>A configurable connector: the probe decides the verdict and sees the context; the scheme is Basic unless set.</summary>
    internal sealed class ProbeConnector(string type, Func<ConnectorTestContext, CancellationToken, Task<ConnectionTestResult>>? probe = null) : IConnector
    {
        public string ConnectorType => type;

        public string DisplayName { get; init; } = "Fake";

        public ConnectorConfigSchema ConfigSchema { get; init; } = ConnectorConfigSchema.Empty;

        public AuthenticationType AuthType { get; init; } = AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind { get; init; } = ConnectorEndpointKind.ConnectionString;

        public bool HostOnly { get; init; }

        public ConnectorTestContext? LastContext { get; private set; }

        public int Calls { get; private set; }

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            Calls++;
            return probe is null ? ConnectionTestResult.Ok() : await probe(context, cancellationToken);
        }
    }

    /// <summary>An extracting connector yielding <paramref name="count"/> records for its one operation, counting what it yielded; an optional fault thrown on the first record.</summary>
    internal sealed class ProbeExtractor(string type, int count, Exception? fault = null, Func<ConnectorExtractContext, CancellationToken, Task>? beforeFirst = null) : IConnector, IConnectorExtractor
    {
        public string ConnectorType => type;

        public string DisplayName => "Fake Extractor";

        public AuthenticationType AuthType { get; init; } = AuthenticationType.None;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public ConnectorExtractContext? LastContext { get; private set; }

        public int Yielded { get; private set; }

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[] { new ConnectorOperation("list", "List") };

        public Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConnectionTestResult.Ok());

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastContext = context;
            if (beforeFirst is not null)
            {
                await beforeFirst(context, cancellationToken);
            }

            if (fault is not null)
            {
                throw fault;
            }

            for (var i = 1; i <= count; i++)
            {
                await Task.Yield();
                Yielded++;
                yield return new ConnectorRecord(new Dictionary<string, object?> { ["op"] = context.OperationName, ["id"] = (long)i });
            }
        }
    }

    internal sealed class RecordingDomainEventHandler<TEvent> : IDomainEventHandler<TEvent>
        where TEvent : IDomainEvent
    {
        public List<TEvent> Received { get; } = new();

        public Task HandleAsync(TEvent domainEvent, CancellationToken ct = default)
        {
            Received.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeUsageProvider(ConnectionUsage usage) : IConnectionUsageProvider
    {
        public Task<ConnectionUsage> GetUsageAsync(Guid connectionId, CancellationToken ct = default) => Task.FromResult(usage);
    }
}
