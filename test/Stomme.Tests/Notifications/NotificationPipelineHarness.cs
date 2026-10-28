using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The HARNESS SWAP for the predecessor platform's dispatch/digest suites (<c>AddInMemoryPersistence</c> + <c>AddMediator</c> + a bus):
    /// the module's own <c>ServerStartup</c> on the in-process FILE store (a real serialize/deserialize per scope), the
    /// shipped channels replaced by <see cref="FakeTransportChannel"/>s, and the two framework-facing ports
    /// (<see cref="INotificationSiteSettings"/>, <see cref="INotificationRecipientDirectory"/>) replaced by in-memory
    /// doubles. Time is a <see cref="ManualTimeProvider"/>. NO <see cref="IEventBus"/> is registered unless asked for -
    /// messaging OFF is the default posture (D100 point 3: the job is authoritative).
    /// </summary>
    internal sealed class NotificationPipelineHarness : IAsyncDisposable
    {
        public const int SiteId = 1;
        public const int FrameworkTenantId = 1;

        public static readonly DateTime Start = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private readonly ServiceProvider _services;

        private NotificationPipelineHarness(string root, ServiceProvider services, ManualTimeProvider clock,
            InMemoryNotificationSiteSettings settings, FakeRecipientDirectory directory, RecordingEventBus? bus)
        {
            _root = root;
            _services = services;
            Clock = clock;
            Settings = settings;
            Directory = directory;
            Bus = bus;
        }

        public ManualTimeProvider Clock { get; }

        public InMemoryNotificationSiteSettings Settings { get; }

        public FakeRecipientDirectory Directory { get; }

        public RecordingEventBus? Bus { get; }

        public IServiceProvider Services => _services;

        public static async Task<NotificationPipelineHarness> StartAsync(
            IEnumerable<INotificationChannel> transports, bool withEventBus = false, Action<IServiceCollection>? configure = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"stomme-notifications-pipeline-{Guid.NewGuid():N}");
            var clock = new ManualTimeProvider(Start);
            var settings = new InMemoryNotificationSiteSettings();
            var directory = new FakeRecipientDirectory();
            var bus = withEventBus ? new RecordingEventBus() : null;

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = root,
                })
                .Build());
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = FrameworkTenantId, SiteId = SiteId, Name = "localhost" } });

            new Stomme.Modules.Notifications.Startup.ServerStartup().ConfigureServices(services);

            services.RemoveAll<INotificationChannel>();
            foreach (var transport in transports)
            {
                services.AddSingleton(transport);
            }

            services.AddSingleton<INotificationSiteSettings>(settings);
            services.AddSingleton<INotificationRecipientDirectory>(directory);
            if (bus is not null)
            {
                services.AddSingleton<IEventBus>(bus);
            }

            configure?.Invoke(services);

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }

            return new NotificationPipelineHarness(root, provider, clock, settings, directory, bus);
        }

        public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
        {
            await using var scope = _services.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }

        public async Task AddAsync<TAggregate>(params TAggregate[] aggregates)
            where TAggregate : AggregateRoot<Guid>
        {
            await using var scope = _services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<TAggregate, Guid>>();
            foreach (var aggregate in aggregates)
            {
                await repository.AddAsync(aggregate);
            }
        }

        public Task<TAggregate?> GetAsync<TAggregate>(Guid id)
            where TAggregate : AggregateRoot<Guid> =>
            InScopeAsync(sp => sp.GetRequiredService<IRepository<TAggregate, Guid>>().GetByIdAsync(id));

        public Task<Guid> DispatchAsync(NotificationDispatchRequest request) =>
            InScopeAsync(sp => sp.GetRequiredService<INotificationDispatcher>().DispatchAsync(request));

        /// <summary>One dispatch-job pass for the site, in a fresh scope (what the job does per site).</summary>
        public Task<NotificationSiteRunResult> RunSiteAsync() =>
            InScopeAsync(sp => sp.GetRequiredService<NotificationDispatchProcessor>().RunSiteAsync(SiteId));

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            try
            {
                if (System.IO.Directory.Exists(_root))
                {
                    System.IO.Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // a leftover temp directory is not worth failing a green run over
            }
        }

        // ---- builders shared by the pipeline suites -------------------------------------------------------------

        public static NotificationTemplateAggregate Template(string key, string subject, string body, params string[] placeholders) =>
            NotificationTemplateAggregate.Create(Guid.NewGuid(), TenantId.Default, SiteId, key,
                supportedChannels: [ChannelKind.Email, ChannelKind.InApp, ChannelKind.Webhook, ChannelKind.Sms],
                contents: [new LocalizedContent(new Locale("en"), new Content(subject, body))],
                placeholders: new PlaceholderSchema(placeholders));

        public static NotificationChannelAggregate Channel(ChannelKind kind, string providerKey, string? name = null) =>
            NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, SiteId, kind, name ?? $"Capturing {kind}",
                new TransportSettings(kind, kind == ChannelKind.Webhook ? "https://hooks.example.com/x" : null, null,
                    [new TransportOption(NotificationProviderKeys.SelectorOption, providerKey)]));

        public static NotificationRequestAggregate Request(string templateKey, IReadOnlyList<Recipient> recipients, IReadOnlyList<ChannelKind> channels,
            NotificationSeverity severity = NotificationSeverity.Information, string? topic = null, IReadOnlyList<NotificationParameter>? parameters = null) =>
            NotificationRequestAggregate.Create(Guid.NewGuid(), TenantId.Default, SiteId, templateKey, recipients, channels,
                parameters, severity, topic, createdOnUtc: Start);

        public static NotificationPreferenceAggregate Preference(int userId, DigestMode digest = DigestMode.Immediate,
            IReadOnlyList<ChannelKind>? accepted = null, NotificationSeverityFilter severity = NotificationSeverityFilter.All,
            IReadOnlyList<string>? muted = null, QuietHours? quietHours = null, string? locale = null)
        {
            var preference = NotificationPreferenceAggregate.Create(Guid.NewGuid(), TenantId.Default, SiteId, userId);
            preference.Configure(accepted ?? [], severity, muted ?? [], quietHours, digest, locale);
            return preference;
        }

        public static Recipient User(int userId) => new(RecipientKind.User, userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A settable clock.</summary>
    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTime startUtc) => _now = new DateTimeOffset(startUtc, TimeSpan.Zero);

        public DateTime UtcNow => _now.UtcDateTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        public void Set(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero);
    }

    internal sealed class InMemoryNotificationSiteSettings : INotificationSiteSettings
    {
        public ConcurrentDictionary<int, bool> ExternalRecipients { get; } = new();

        public ConcurrentDictionary<int, TimeSpan> Retention { get; } = new();

        public ConcurrentDictionary<int, DateTime> LastHourlyPass { get; } = new();

        public Task<bool> AllowsExternalRecipientsAsync(int siteId, CancellationToken ct = default) =>
            Task.FromResult(ExternalRecipients.TryGetValue(siteId, out var allowed) && allowed);

        public Task<TimeSpan> GetRequestRetentionAsync(int siteId, CancellationToken ct = default) =>
            Task.FromResult(Retention.TryGetValue(siteId, out var retention) ? retention : TimeSpan.FromDays(NotificationDispatchProcessor.DefaultRequestRetentionDays));

        public Task<DateTime?> GetLastHourlyPassAsync(int siteId, CancellationToken ct = default) =>
            Task.FromResult(LastHourlyPass.TryGetValue(siteId, out var last) ? last : (DateTime?)null);

        public Task SetLastHourlyPassAsync(int siteId, DateTime passedOnUtc, CancellationToken ct = default)
        {
            LastHourlyPass[siteId] = passedOnUtc;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeRecipientDirectory : INotificationRecipientDirectory
    {
        /// <summary>(site, user) pairs that are users of the site.</summary>
        public HashSet<(int SiteId, int UserId)> SiteUsers { get; } = [];

        public Task<bool> IsSiteUserAsync(int siteId, int userId, CancellationToken ct = default) =>
            Task.FromResult(SiteUsers.Contains((siteId, userId)));
    }

    internal sealed class RecordingEventBus : IEventBus
    {
        public ConcurrentQueue<object> SentLocal { get; } = new();

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : class => Task.CompletedTask;

        public Task PublishAsync<TEvent>(TEvent @event, IDictionary<string, string>? headers = null, CancellationToken ct = default) where TEvent : class => Task.CompletedTask;

        public Task SendAsync<TCommand>(TCommand command, CancellationToken ct = default) where TCommand : class => Task.CompletedTask;

        public Task SendAsync<TCommand>(TCommand command, string destinationQueue, CancellationToken ct = default) where TCommand : class => Task.CompletedTask;

        public Task SendLocalAsync<TCommand>(TCommand command, CancellationToken ct = default) where TCommand : class
        {
            SentLocal.Enqueue(command);
            return Task.CompletedTask;
        }

        public Task DeferAsync<TMessage>(TimeSpan delay, TMessage message, CancellationToken ct = default) where TMessage : class => Task.CompletedTask;
    }
}
