using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Guestbook.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The clean-arch module persistence provider-neutrality GATE. It closes the exact hole that let the
    /// Guestbook sample ship EF/SQLite-only: a clean-arch module authored in the
    /// FeatureManagement shape works on a relational install and is silently broken on a RavenDB install,
    /// GREEN on the compiler and every EF test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IT DISCOVERS. Every concrete <see cref="AggregateRoot{TId}"/> in the host's MODULE namespaces
    /// (<c>Stomme.Modules.*</c> and <c>Stomme.FeatureManagement.*</c>) — reflectively, no hand-maintained
    /// list — and every module <see cref="IServerStartup"/>. For each startup composed in ISOLATION under a
    /// simulated relational config, the aggregates whose clean-arch <c>IRepository&lt;TAggregate, TId&gt;</c>
    /// that startup registers (closed OR via the open generic) are IN SCOPE; the gate then asserts the SAME
    /// startup registers them under a simulated RavenDB config. A seam-backed module (HtmlText registers a
    /// custom <c>IHtmlTextStore</c>, never the plain <c>IRepository&lt;HtmlTextAggregate, int&gt;</c>) registers
    /// no plain repository and is correctly out of scope — the gate targets the clean-arch stack only.
    /// </para>
    /// <para>
    /// WHY ISOLATION. FeatureManagement owns the OPEN-generic <c>IRepository&lt;,&gt;</c> on both providers, so
    /// co-composing it with Guestbook would let its open generic resolve <c>IRepository&lt;Guestbook, Guid&gt;</c>
    /// on RavenDB even if Guestbook registered nothing — masking the very bug. Each startup is composed alone, so
    /// Guestbook's own composition is what proves (or, if its Raven branch were removed, disproves) its
    /// neutrality.
    /// </para>
    /// <para>
    /// VALIDATED BOTH DIRECTIONS: green with the Guestbook Raven branch present; RED when the
    /// <c>else</c> branch of <c>Guestbook/Startup/ServerStartup</c> is removed so a RavenDB install registers no
    /// Guestbook repository (the closed descriptor vanishes under the Raven config) — reverted after observing.
    /// </para>
    /// </remarks>
    public class ModuleAggregateProviderNeutralityTests
    {
        private const string RelationalType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";

        // A closed IRepository<agg,id> descriptor OR the open generic IRepository<,> counts as "the clean-arch
        // repository resolves for this aggregate": MS.DI resolves a request for the closed type via a closed
        // registration in preference to the open generic (verified), and via the open generic otherwise.
        private static bool RepositoryResolves(IServiceCollection services, Type aggregate, Type id)
        {
            var closed = typeof(IRepository<,>).MakeGenericType(aggregate, id);
            return services.Any(descriptor =>
                descriptor.ServiceType == closed || descriptor.ServiceType == typeof(IRepository<,>));
        }

        private static bool ClosedRepositoryRegistered(IServiceCollection services, Type aggregate, Type id)
        {
            var closed = typeof(IRepository<,>).MakeGenericType(aggregate, id);
            return services.Any(descriptor => descriptor.ServiceType == closed);
        }

        private static IConfigurationRoot Config(string databaseType) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:DefaultDBType"] = databaseType,
                ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
            })
            .Build();

        // Compose ONE module startup in isolation under the given provider. The IConfigurationRoot is registered
        // as an instance the way the host does, so a startup's compose-time provider read sees it.
        private static ServiceCollection Compose(IServerStartup startup, string databaseType)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationRoot>(Config(databaseType));
            startup.ConfigureServices(services);
            return services;
        }

        private static IReadOnlyList<Type> ModuleServerStartupTypes() =>
            typeof(FrameworkStoreProviderSelection).Assembly
                .GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(IServerStartup).IsAssignableFrom(type)
                    && type.Namespace != null
                    && type.Namespace.StartsWith("Stomme.Modules.", StringComparison.Ordinal)
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

        // (aggregate, id) for every concrete AggregateRoot<> declared in a module namespace across the loaded
        // Stomme.* assemblies.
        private static IReadOnlyList<(Type Aggregate, Type Id)> ModuleAggregates() =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.FullName is { } name
                    && name.Contains("Stomme", StringComparison.OrdinalIgnoreCase))
                .SelectMany(SafeGetTypes)
                .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
                .Where(type => type.Namespace != null
                    && (type.Namespace.StartsWith("Stomme.Modules.", StringComparison.Ordinal)
                        || type.Namespace.StartsWith("Stomme.FeatureManagement", StringComparison.Ordinal)))
                .Select(type => (type, id: AggregateIdType(type)))
                .Where(pair => pair.id is not null)
                .Select(pair => (pair.type, pair.id!))
                .OrderBy(pair => pair.type.FullName, StringComparer.Ordinal)
                .ToList();

        private static Type? AggregateIdType(Type type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
                {
                    return current.GetGenericArguments()[0];
                }
            }

            return null;
        }

        private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type is not null)!;
            }
        }

        // Discovery must not silently find nothing (the vacuity guard): the theories would pass by running
        // no cases. Asserted against the two clean-arch aggregates known to exist, plus a floor.
        [Fact]
        public void Discovery_finds_the_module_aggregates_and_startups()
        {
            var aggregates = ModuleAggregates().Select(pair => pair.Aggregate).ToList();
            var startups = ModuleServerStartupTypes();

            Assert.Contains(typeof(GuestbookEntryAggregate), aggregates);
            Assert.Contains(aggregates, type => type.Name == "FeatureFlag");
            Assert.True(aggregates.Count >= 2, $"Discovery found only {aggregates.Count} module aggregate(s).");
            Assert.Contains(startups, type => type.FullName == "Stomme.Modules.Guestbook.Startup.ServerStartup");
        }

        // The gate is not vacuous in the direction that matters: the Guestbook startup, composed alone under a
        // relational config, registers the CLOSED IRepository<GuestbookEntryAggregate, Guid> — so at least one
        // aggregate is genuinely IN SCOPE through a closed registration, not merely via FeatureManagement's open
        // generic. This is the exact registration whose Raven counterpart the gate then demands.
        [Fact]
        public void The_guestbook_startup_registers_the_closed_repository_under_a_relational_config()
        {
            var guestbook = (IServerStartup)Activator.CreateInstance(
                Type.GetType("Stomme.Modules.Guestbook.Startup.ServerStartup, Stomme.Server")!)!;

            var relational = Compose(guestbook, RelationalType);

            Assert.True(ClosedRepositoryRegistered(relational, typeof(GuestbookEntryAggregate), typeof(Guid)),
                "The Guestbook module must register a CLOSED IRepository<GuestbookEntryAggregate, Guid> on a relational install.");
        }

        public static TheoryData<Type> ModuleStartups()
        {
            var data = new TheoryData<Type>();
            foreach (var startup in ModuleServerStartupTypes())
            {
                data.Add(startup);
            }

            return data;
        }

        // THE GATE. Every module aggregate a startup makes clean-arch-persistable on a RELATIONAL install, it
        // must also make persistable on a RAVENDB install — proven by composing the SAME startup under each
        // simulated provider config. A clean-arch module that ships an EF branch and no Raven branch fails here.
        [Theory]
        [MemberData(nameof(ModuleStartups))]
        public void A_module_registers_its_clean_arch_aggregate_repositories_on_both_providers(Type startupType)
        {
            var startup = (IServerStartup)Activator.CreateInstance(startupType)!;
            var relational = Compose(startup, RelationalType);
            var raven = Compose(startup, RavenType);

            var missingOnRaven = ModuleAggregates()
                .Where(pair => RepositoryResolves(relational, pair.Aggregate, pair.Id))
                .Where(pair => !RepositoryResolves(raven, pair.Aggregate, pair.Id))
                .Select(pair => pair.Aggregate.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.True(missingOnRaven.Count == 0,
                $"{startupType.FullName} wires these aggregate repositories on a relational install but NOT on a " +
                $"RavenDB install (the EF-only drift D76 exists to stop): {string.Join(", ", missingOnRaven)}");
        }
    }
}
