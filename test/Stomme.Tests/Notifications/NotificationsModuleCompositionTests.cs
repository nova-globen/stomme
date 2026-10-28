using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Mediator;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications.Persistence;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// BL-78 - the Notifications module startup, composed ALONE under each provider config, registers the CLOSED
    /// <c>IRepository&lt;TAggregate, Guid&gt;</c> for all four aggregates plus the four closed module repositories,
    /// and does NOT call <c>AddMediator</c> (F13: Guestbook already did, and it is additive). This closes the
    /// direction <c>ModuleAggregateProviderNeutralityTests</c> cannot see: that gate only compares relational vs
    /// RavenDB, so an aggregate a startup forgets on BOTH providers is out of its scope and passes silently.
    /// </summary>
    public sealed class NotificationsModuleCompositionTests
    {
        private static readonly Type[] Aggregates =
        [
            typeof(NotificationRequestAggregate),
            typeof(NotificationTemplateAggregate),
            typeof(NotificationChannelAggregate),
            typeof(NotificationPreferenceAggregate),
        ];

        public static TheoryData<string> Providers() => new()
        {
            "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
            "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server",
            "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server",
            "Stomme.Database.File.FileDatabase, Stomme.Server",
        };

        private static ServiceCollection Compose(string databaseType)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = databaseType,
                    ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
                })
                .Build());
            new Stomme.Modules.Notifications.Startup.ServerStartup().ConfigureServices(services);
            return services;
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void The_startup_registers_every_aggregate_repository_and_every_closed_module_repository(string databaseType)
        {
            var services = Compose(databaseType);

            var missing = Aggregates
                .Where(aggregate => !services.Any(d => d.ServiceType == typeof(IRepository<,>).MakeGenericType(aggregate, typeof(Guid))))
                .Select(aggregate => aggregate.Name)
                .ToList();
            Assert.True(missing.Count == 0, $"No closed IRepository<,Guid> registered for: {string.Join(", ", missing)}");

            Assert.Contains(services, d => d.ServiceType == typeof(INotificationRequestRepository) && d.ImplementationType == typeof(NotificationRequestRepository));
            Assert.Contains(services, d => d.ServiceType == typeof(INotificationTemplateRepository) && d.ImplementationType == typeof(NotificationTemplateRepository));
            Assert.Contains(services, d => d.ServiceType == typeof(INotificationChannelRepository) && d.ImplementationType == typeof(NotificationChannelRepository));
            Assert.Contains(services, d => d.ServiceType == typeof(INotificationPreferenceRepository) && d.ImplementationType == typeof(NotificationPreferenceRepository));
        }

        [Fact]
        public void The_startup_does_not_add_the_mediator_or_its_behaviors()
        {
            var services = Compose("Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server");

            Assert.DoesNotContain(services, d => d.ServiceType == typeof(IMediator));
            Assert.DoesNotContain(services, d => d.ServiceType.IsGenericType
                && d.ServiceType.GetGenericTypeDefinition().FullName == "Stomme.Mediator.IPipelineBehavior`2");
        }

        [Fact]
        public void The_aggregates_live_in_the_module_namespace_and_the_repositories_outside_it()
        {
            Assert.All(Aggregates, aggregate => Assert.Equal("Stomme.Modules.Notifications.Domain", aggregate.Namespace));
            Assert.All(
                new[] { typeof(INotificationRequestRepository), typeof(INotificationTemplateRepository), typeof(INotificationChannelRepository), typeof(INotificationPreferenceRepository) },
                contract => Assert.False(contract.Namespace!.StartsWith("Stomme.Modules.", StringComparison.Ordinal)));
        }
    }
}
