#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Infrastructure;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-111 (D79's correction) - a module aggregate is TENANT-OWNED by contract. The shared module store is one per
    /// installation and a module-scoped key (a module id) repeats across tenants, so a non-tenant-owned aggregate let
    /// module 5 of tenant one read module 5 of tenant two on EF and RavenDB (measured; the File store partitions by
    /// tenant). In-tree this is a build gate: every module <c>AggregateRoot&lt;&gt;</c> implements
    /// <see cref="ITenantOwned"/>. An external module's aggregate cannot be gated at build; it is composed all the same
    /// and NAMED at startup by a <see cref="ModuleAggregateTenancyNotice"/> (never a failure - the BL-105 posture), which
    /// the second test pins in both directions.
    /// </summary>
    public class ModuleAggregateTenancyGateTests(ITestOutputHelper output)
    {
        /// <summary>
        /// The aggregates that are NOT tenant-owned for a stated reason. The BL-111 row said the Guestbook's was the only
        /// one in the tree; this gate falsified that on its first run (2026-09-27) with these three. A stale entry - an
        /// aggregate that became tenant-owned or disappeared - fails, so the list cannot rot.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> NotTenantOwnedByDesign = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Stomme.Modules.Backup.Domain.BackupScheduleAggregate"] = "installation-wide by design (D106): ONE schedule per installation, set by the Host; a backup is of the whole installation, so every tenant's Host sees the same row on purpose",
            ["Stomme.Modules.Backup.Domain.BackupSnapshotAggregate"] = "installation-wide by design (D106): a snapshot is of the whole installation and is listed to the Host only",
            ["Stomme.Modules.HtmlText.Repository.HtmlTextAggregate"] = "not in the shared module store: the integer-keyed framework seam (D55), whose relational context is the TENANT's database (HtmlTextContext, IMultiDatabase) - isolation is the database, not a column; on RavenDB it follows the framework seam's tenancy shape (one tenant per database, BL-111's open half)",
        };

        [Fact]
        public void Every_module_aggregate_is_tenant_owned_or_not_for_a_stated_reason()
        {
            var aggregates = ModuleAggregates();
            Assert.True(aggregates.Count >= 10, "The discovery must find the modules' aggregates; found " + aggregates.Count);
            foreach (var aggregate in aggregates)
            {
                output.WriteLine($"{aggregate.FullName}: {(typeof(ITenantOwned).IsAssignableFrom(aggregate) ? "tenant-owned" : "NOT tenant-owned")}");
            }

            var unowned = aggregates.Where(aggregate => !typeof(ITenantOwned).IsAssignableFrom(aggregate)).Select(aggregate => aggregate.FullName!).ToList();
            var unaccounted = unowned.Where(name => !NotTenantOwnedByDesign.ContainsKey(name)).ToList();
            var stale = NotTenantOwnedByDesign.Keys.Where(name => !unowned.Contains(name)).ToList();
            Assert.True(unaccounted.Count == 0 && stale.Count == 0,
                "Module aggregates that are not tenant-owned and not listed with a reason - on a multi-tenant installation their rows are shared by every tenant of the store (BL-111): " + string.Join(", ", unaccounted) + Environment.NewLine +
                "Stale entries (tenant-owned now, or gone): " + string.Join(", ", stale));
            foreach (var (name, reason) in NotTenantOwnedByDesign)
            {
                Assert.True(reason.Length > 40, name + " needs a real reason.");
            }
        }

        [Fact]
        public void The_storage_blind_registration_names_a_non_tenant_owned_aggregate_and_only_that()
        {
            var services = Compose();
            services.AddModuleAggregatePersistence<GuestbookEntryAggregate, Guid>();
            services.AddModuleAggregatePersistence<NotTenantOwnedAggregate, Guid>();
            // The host's own installation-wide aggregate is the build gate's (the reasoned list above), never a startup warning.
            services.AddModuleAggregatePersistence<Stomme.Modules.Backup.Domain.BackupScheduleAggregate, Guid>();

            var notices = services
                .Where(descriptor => descriptor.ServiceType == typeof(ModuleAggregateTenancyNotice))
                .Select(descriptor => (ModuleAggregateTenancyNotice)descriptor.ImplementationInstance!)
                .ToList();

            Assert.Equal(new[] { typeof(NotTenantOwnedAggregate) }, notices.Select(notice => notice.AggregateType));
        }

        private static ServiceCollection Compose()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
                })
                .Build());
            return services;
        }

        // The neutrality gate's discovery (ModuleAggregateProviderNeutralityTests): every AggregateRoot<> under a
        // module namespace or FeatureManagement, in any loaded Stomme assembly.
        private static IReadOnlyList<Type> ModuleAggregates() =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.FullName is { } name && name.Contains("Stomme", StringComparison.OrdinalIgnoreCase))
                .SelectMany(SafeGetTypes)
                .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
                .Where(type => type.Namespace != null
                    && (type.Namespace.StartsWith("Stomme.Modules.", StringComparison.Ordinal)
                        || type.Namespace.StartsWith("Stomme.FeatureManagement", StringComparison.Ordinal)))
                .Where(IsAggregateRoot)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

        private static bool IsAggregateRoot(Type type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
                {
                    return true;
                }
            }

            return false;
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

        /// <summary>The planted counter-example: an aggregate with no tenant, outside any module namespace.</summary>
        public sealed class NotTenantOwnedAggregate : AggregateRoot<Guid>
        {
            public string Text { get; private set; } = string.Empty;
        }
    }
}
