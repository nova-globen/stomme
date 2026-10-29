using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// Shared aggregate/value-object builders for the Notifications domain tests - lifted from the predecessor platform's
    /// <c>NotificationTestData</c> with the types adapted: every aggregate is site-scoped (<see cref="SiteId"/>),
    /// a secret is an <c>ISiteSecretStore</c> REFERENCE KEY rather than a <c>vault://</c> URI, and a user
    /// recipient is an int user id.
    /// </summary>
    internal static class NotificationTestData
    {
        public const int SiteId = 1;

        /// <summary>A well-formed <c>ISiteSecretStore</c> reference key (the D101 key rule).</summary>
        public const string SecretReference = "notifications.channel.primary.credential";

        public static Recipient Email(string address = "ops@example.com") => new(RecipientKind.Email, address);

        public static TransportSettings EmailTransport(string? secretReference = SecretReference) =>
            new(ChannelKind.Email, endpoint: "smtp.example.com", secretReference: secretReference,
                options: [new TransportOption("port", "587"), new TransportOption("from", "no-reply@example.com")]);

        public static TransportSettings WebhookTransport(string url = "https://hooks.example.com/notify") =>
            new(ChannelKind.Webhook, endpoint: url, secretReference: null, options: []);

        public static TransportSettings InAppTransport() =>
            new(ChannelKind.InApp, endpoint: null, secretReference: null, options: []);

        public static NotificationChannelAggregate EmailChannel(string name = "Primary email") =>
            NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, SiteId, ChannelKind.Email, name, EmailTransport());

        public static NotificationChannelAggregate WebhookChannel(string name = "Ops webhook", string url = "https://hooks.example.com/notify") =>
            NotificationChannelAggregate.Create(Guid.NewGuid(), TenantId.Default, SiteId, ChannelKind.Webhook, name, WebhookTransport(url));

        public static NotificationTemplateAggregate Template(string key = "pipeline.failed") =>
            NotificationTemplateAggregate.Create(
                Guid.NewGuid(), TenantId.Default, SiteId, key,
                supportedChannels: [ChannelKind.Email, ChannelKind.InApp],
                contents:
                [
                    new LocalizedContent(new Locale("en"), new Content("Pipeline {{ name }} failed", "Run {{ runId }} failed.")),
                ],
                placeholders: new PlaceholderSchema(["name", "runId"]));

        public static NotificationRequestAggregate Request(params ChannelKind[] channels) =>
            NotificationRequestAggregate.Create(
                Guid.NewGuid(), TenantId.Default, SiteId, "pipeline.failed",
                recipients: [Email()],
                requestedChannels: channels.Length == 0 ? [ChannelKind.Email] : channels);
    }

    /// <summary>
    /// The HARNESS SWAP for the predecessor platform's <c>services.AddInMemoryPersistence()</c> round-trip facts: the module's own
    /// <c>ServerStartup</c> composed on the in-process FILE store (a throwaway directory), so a "round-trips through
    /// the repository" fact goes through a real serialize/deserialize (the predecessor platform's in-memory store never serialized).
    /// Every write and every read runs in its own DI scope.
    /// </summary>
    internal sealed class NotificationFileStore : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _services;

        private NotificationFileStore(string root, ServiceProvider services)
        {
            _root = root;
            _services = services;
        }

        public static async Task<NotificationFileStore> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"stomme-notifications-file-{Guid.NewGuid():N}");
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = root,
                })
                .Build());
            new Stomme.Modules.Notifications.Startup.ServerStartup().ConfigureServices(services);
            var provider = services.BuildServiceProvider();
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }

            return new NotificationFileStore(root, provider);
        }

        public async Task AddAsync<TAggregate>(TAggregate aggregate)
            where TAggregate : AggregateRoot<Guid>
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IRepository<TAggregate, Guid>>().AddAsync(aggregate);
        }

        public async Task<TAggregate?> GetByIdAsync<TAggregate>(Guid id)
            where TAggregate : AggregateRoot<Guid>
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IRepository<TAggregate, Guid>>().GetByIdAsync(id);
        }

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
                // A leftover temp directory is not worth failing a green run over.
            }
        }
    }
}
