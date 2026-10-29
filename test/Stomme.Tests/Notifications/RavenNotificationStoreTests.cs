using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The 4 RavenDB cases: the four notification aggregates round-trip through a LIVE RavenDB with their nested value/entity
    /// graphs - the persistence proof an in-memory store cannot give. Assertions kept; types adapted (site id, int user
    /// id, UTC DateTime, the secret REFERENCE KEY).
    /// <para>
    /// <b>Location.</b> These could sit in <c>test/Stomme.Persistence.Raven.Tests</c>, but that
    /// project references no <c>Stomme.Server</c> (where the aggregates live) - only Persistence.RavenDb /
    /// Abstractions / SharedKernel / Tenancy. Rather than hang the whole host off a leaf persistence test project they
    /// live here as <c>RavenNotificationStoreTests</c>, against the RavenDB test server via <c>RAVENDB_TEST_URL</c>, composed the
    /// way the host composes them (the module <c>ServerStartup</c> under a RavenDB config). A per-class scratch database
    /// replaces a container fixture. Fails, never skips, when RavenDB is unreachable.
    /// </para>
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenNotificationStoreTests : IAsyncLifetime
    {
        private static readonly DateTime At = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-notifications-raven-store-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-notifications-raven-store-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server",
                })
                .Build());
            services.AddRavenFrameworkStore(_store);
            new Stomme.Modules.Notifications.Startup.ServerStartup().ConfigureServices(services);
            _services = services.BuildServiceProvider();
            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
                // best-effort: the next run's janitor sweep removes a leftover
            }
        }

        // Each call gets its own DI scope, so a fresh RavenRepository is built per call.
        private async Task AddAsync<T>(T aggregate)
            where T : AggregateRoot<Guid>
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IRepository<T, Guid>>().AddAsync(aggregate);
        }

        private async Task<T?> GetByIdAsync<T>(Guid id)
            where T : AggregateRoot<Guid>
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IRepository<T, Guid>>().GetByIdAsync(id);
        }

        [Fact]
        public async Task NotificationRequest_WithAttempts_RoundTripsThroughRaven()
        {
            var request = NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "pipeline.failed",
                recipients: [new Recipient(RecipientKind.Email, "ops@example.com")],
                requestedChannels: [ChannelKind.Email, ChannelKind.InApp]);
            request.RecordAttempt(ChannelKind.Email, DeliveryStatus.Succeeded, At);
            request.RecordAttempt(ChannelKind.InApp, DeliveryStatus.Failed, At, "hub offline");

            await AddAsync(request);

            var loaded = await GetByIdAsync<NotificationRequestAggregate>(request.Id);

            Assert.NotNull(loaded);
            Assert.Equal(NotificationStatus.PartiallyFailed, loaded!.Status);
            Assert.Equal([ChannelKind.Email, ChannelKind.InApp], loaded.RequestedChannels);
            Assert.Equal(2, loaded.Attempts.Count);
            Assert.Equal("hub offline", loaded.Attempts.Single(a => a.Channel == ChannelKind.InApp).Error);
            Assert.Equal(DeliveryStatus.Succeeded, loaded.Attempts.Single(a => a.Channel == ChannelKind.Email).Status);
        }

        [Fact]
        public async Task NotificationChannel_WithTransportAndRateLimit_RoundTripsThroughRaven()
        {
            var channel = NotificationChannelAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, ChannelKind.Email, "Primary email",
                new TransportSettings(ChannelKind.Email, "smtp.example.com", NotificationTestData.SecretReference,
                    options: [new TransportOption("port", "587"), new TransportOption("from", "no-reply@example.com")]),
                new RateLimit(100, TimeSpan.FromHours(1)));

            await AddAsync(channel);

            var loaded = await GetByIdAsync<NotificationChannelAggregate>(channel.Id);

            Assert.NotNull(loaded);
            Assert.True(loaded!.Enabled);
            Assert.Equal(ChannelKind.Email, loaded.Kind);
            Assert.Equal(NotificationTestData.SecretReference, loaded.Transport.SecretReference);
            Assert.Equal(channel.Transport, loaded.Transport);
            Assert.Equal(channel.RateLimit, loaded.RateLimit);
        }

        [Fact]
        public async Task NotificationTemplate_WithLocalizedContent_RoundTripsThroughRaven()
        {
            var template = NotificationTemplateAggregate.Create(
                Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, "pipeline.failed",
                supportedChannels: [ChannelKind.Email, ChannelKind.InApp],
                contents:
                [
                    new LocalizedContent(new Locale("en"), new Content("Pipeline {{ name }} failed", "Run {{ runId }} failed.")),
                    new LocalizedContent(new Locale("fr"), new Content("Échec du pipeline {{ name }}", "Le run {{ runId }} a échoué.")),
                ],
                placeholders: new PlaceholderSchema(["name", "runId"]));

            await AddAsync(template);

            var loaded = await GetByIdAsync<NotificationTemplateAggregate>(template.Id);

            Assert.NotNull(loaded);
            Assert.Equal("pipeline.failed", loaded!.Key);
            Assert.Equal(2, loaded.Contents.Count);
            Assert.Equal(template.Contents.Single(c => c.Locale.Code == "fr").Content,
                loaded.Contents.Single(c => c.Locale.Code == "fr").Content);
            Assert.Equal(template.Placeholders, loaded.Placeholders);
        }

        [Fact]
        public async Task NotificationPreference_WithQuietHoursAndFilters_RoundTripsThroughRaven()
        {
            var preference = NotificationPreferenceAggregate.Create(Guid.NewGuid(), TenantId.Default, NotificationTestData.SiteId, 4242);
            preference.Configure(
                acceptedChannels: [ChannelKind.Email, ChannelKind.Webhook],
                severityFilter: NotificationSeverityFilter.ErrorsOnly,
                mutedTopics: ["pipeline:42"],
                quietHours: new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), TimeSpan.FromHours(2)),
                digest: DigestMode.Digest,
                preferredLocale: "fr");

            await AddAsync(preference);

            var loaded = await GetByIdAsync<NotificationPreferenceAggregate>(preference.Id);

            Assert.NotNull(loaded);
            Assert.Equal(preference.UserId, loaded!.UserId);
            Assert.Equal([ChannelKind.Email, ChannelKind.Webhook], loaded.AcceptedChannels);
            Assert.Equal(NotificationSeverityFilter.ErrorsOnly, loaded.SeverityFilter);
            Assert.Equal(["pipeline:42"], loaded.MutedTopics);
            Assert.Equal(DigestMode.Digest, loaded.Digest);
            Assert.Equal("fr", loaded.PreferredLocale);
            Assert.Equal(preference.QuietHours, loaded.QuietHours);
        }
    }
}
