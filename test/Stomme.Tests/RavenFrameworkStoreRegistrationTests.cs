using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Persistence.EfCore;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Xunit;

namespace Stomme.Tests
{
    // AddRavenFrameworkStore's registration surface. No live server: nothing here connects — the
    // document store is only captured, and the id generator is built behind a factory lambda — so these run
    // in the normal suite rather than needing the RavenDB container the conformance suite uses.
    //
    // The subject is the hazard, not the happy path. Registering the framework store's RavenDB provider means
    // adding a SECOND persistence provider to a host that already has one, and the ways that goes wrong are
    // silent: a stray open-generic registration re-points live feature flags with no error, no warning and a
    // green build.
    [Trait("Category", "ExternalDatabase")]
    public class RavenFrameworkStoreRegistrationTests
    {
        private static ServiceCollection NewServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton<Tenancy.Abstractions.ITenantContext>(new SystemScopeTenantContext());
            return services;
        }

        // Never initialized, never contacted: AddRavenFrameworkStore must not touch the network.
        private static IDocumentStore NewStore() => new DocumentStore { Urls = ["http://localhost:0"], Database = "unused" };

        // THE test. AddFeatureManagement owns the open-generic IRepository<,> in this host (via
        // AddEfCorePersistence<FeatureManagementDbContext>) and points it at the feature module's own SQLite
        // store. DI is last-wins, so a second open-generic registration silently re-points every live feature
        // flag at the framework store — no exception, nothing in a log, flags simply read from a database that
        // has never held one.
        //
        // AddRavenPersistence DOES register it (services.AddScoped(typeof(IRepository<,>),
        // typeof(RavenRepository<,>))), which is exactly why AddRavenFrameworkStore must not call it and must
        // register closed interfaces only. See D17 rule 1 and src/Stomme.Persistence.RavenDb/README.md.
        [Fact]
        public void The_raven_framework_store_never_registers_the_open_generic_repository()
        {
            var services = NewServices();

            services.AddRavenFrameworkStore(NewStore());

            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IRepository<,>));
        }

        // The same rule for the ambient session factory and DbContext: AddEfCorePersistence owns both, and the
        // framework store reads through its OWN two closed factories precisely so it never has to take them.
        [Fact]
        public void The_raven_framework_store_never_registers_the_ambient_session_factory_or_a_dbcontext()
        {
            var services = NewServices();

            services.AddRavenFrameworkStore(NewStore());

            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(Persistence.Abstractions.IDocumentSessionFactory));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(DbContext));
        }

        // The behavioural half of the same hazard, and the one that would actually be observed in production:
        // with BOTH providers registered, a resolved IRepository<,> must still be the EF one — i.e. the
        // feature flags stay where AddFeatureManagement put them. This fails if AddRavenFrameworkStore ever
        // grows an open-generic registration, whichever order the two extensions are called in.
        [Fact]
        public void Registering_the_raven_framework_store_leaves_feature_flags_on_their_own_store()
        {
            var services = NewServices();
            // Stand in for AddFeatureManagement's registration: the open generic, owned by EF Core.
            services.AddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));

            services.AddRavenFrameworkStore(NewStore());

            var repository = services
                .Last(descriptor => descriptor.ServiceType == typeof(IRepository<,>))
                .ImplementationType;

            Assert.Equal(typeof(EfRepository<,>), repository);
        }

        // --- the seam's contract surface, discovered rather than listed ---
        //
        // WHY REFLECTIVE. The list this replaced was eight hand-written [InlineData] lines and a comment
        // asserting they were "all present". It was true when written and it could never notice becoming
        // false: D24 added IPermissionStore to the EF provider, nobody added the Raven twin, and this test
        // kept passing because the interface it never mentioned could not fail a case it never ran. An
        // enumerated gate can only re-state what its author already knew; it cannot gate what comes next.
        //
        // WHY THE INTERFACE SET, NOT THE EF REGISTRATIONS. Reflecting over the EF provider and demanding a
        // Raven counterpart would fix the observed defect and enshrine its cause: it makes EF the source of
        // truth and Raven the follower, and it is blind in the mirror direction (a store registered on Raven
        // but not EF). The invariant that actually holds is symmetric and belongs to neither provider —
        // EVERY closed contract interface the seam declares has an implementation on EVERY provider. That is
        // the definition of a seam. So the contract surface is discovered from the DECLARING ASSEMBLY, and
        // both providers are held to it.
        //
        // The discovery rule is the same one ISiteStore.cs documents in prose: a closed, framework-store
        // -specific interface over IRepository<,> or IDocumentSessionFactory. Add a cluster's interface to
        // that file and this gate starts demanding it of both providers on the next run, with no test edit.
        public static TheoryData<Type> ClosedStoreContracts()
        {
            var contracts = new TheoryData<Type>();
            foreach (var contract in DiscoverClosedStoreContracts())
            {
                contracts.Add(contract);
            }

            return contracts;
        }

        private static IEnumerable<Type> DiscoverClosedStoreContracts() =>
            typeof(ISiteStore).Assembly
                .GetTypes()
                .Where(type => type.IsInterface && type.IsPublic)
                .Where(type => type.Namespace == typeof(ISiteStore).Namespace)
                .Where(IsClosedStoreContract)
                .OrderBy(type => type.Name, StringComparer.Ordinal);

        // A contract of the framework store: it closes the seam's open-generic repository over one aggregate,
        // it is one of D19/D23's read-path session factories, or it is D50's ISearchQueryStore. The open
        // generics themselves are excluded — they are the seam's own vocabulary, not this store's surface, and
        // registering them here is the very thing the tests above forbid.
        //
        // ISearchQueryStore is named explicitly because the derivation rule below CANNOT see it, and that gap
        // was found by asking rather than assumed away: it is a closed framework-store contract with an
        // implementation on each provider — the exact thing this gate exists to hold both providers to — but
        // it is deliberately NOT an IRepository<,>, because the read it serves is a prefix filter plus a
        // runtime-keyed aggregation rather than CRUD over an aggregate (see ISearchQueryStore). Discovering
        // only IRepository<,> descendants would have left the seam's single most provider-divergent contract
        // ungated, which is precisely the drift D42 was written to stop. This EXTENDS the rule; it does not
        // weaken it.
        private static bool IsClosedStoreContract(Type type)
        {
            if (type == typeof(IFrameworkStoreSessionFactory)
                || type == typeof(IMasterStoreSessionFactory)
                || type == typeof(ISearchQueryStore))
            {
                return true;
            }

            return type.GetInterfaces().Any(implemented =>
                implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(IRepository<,>));
        }

        // The discovery must not silently find nothing: a rule that matches zero types would make every
        // theory below vacuous and the whole gate would pass by running no cases at all. Asserted against a
        // floor, not an exact count — an exact count is the hand-maintained list wearing a disguise, and
        // would have to be edited by the same person who forgets to add the store.
        [Fact]
        public void The_contract_discovery_finds_the_framework_stores_it_is_meant_to_gate()
        {
            var contracts = DiscoverClosedStoreContracts().ToList();

            Assert.Contains(typeof(ISiteStore), contracts);
            Assert.Contains(typeof(IPermissionStore), contracts);
            Assert.Contains(typeof(IFrameworkStoreSessionFactory), contracts);

            // D50's contract, which is NOT an IRepository<,> and so is matched by name rather than by
            // derivation. Asserted here so that deleting the special case in IsClosedStoreContract fails
            // loudly instead of silently shrinking the gate's surface by one.
            Assert.Contains(typeof(ISearchQueryStore), contracts);

            Assert.DoesNotContain(typeof(IRepository<,>), contracts);
            Assert.True(contracts.Count >= 9, $"Discovery found only {contracts.Count} contracts; the seam declares at least 9.");
        }

        // THE GATE. Every closed contract the seam declares is registered by the RavenDB provider.
        // Symmetrically enforced with the EF theory below: neither provider is the reference, the INTERFACE
        // SET is. This is the test that was red on IPermissionStore before D42 added RavenPermissionStore.
        [Theory]
        [MemberData(nameof(ClosedStoreContracts))]
        public void The_raven_framework_store_registers_every_closed_store_interface(Type serviceType)
        {
            var services = NewServices();

            services.AddRavenFrameworkStore(NewStore());

            Assert.Contains(services, descriptor => descriptor.ServiceType == serviceType);
        }

        // The mirror. The blind spot the enumerated list had ran EF -> Raven; a gate that only ran that way
        // would have the same blind spot pointing the other way. A store added to Raven and not EF is the
        // same defect with the providers swapped, and it fails here.
        [Theory]
        [MemberData(nameof(ClosedStoreContracts))]
        public void The_ef_framework_store_registers_every_closed_store_interface(Type serviceType)
        {
            var services = NewServices();

            services.AddStommeFrameworkStore();

            Assert.Contains(services, descriptor => descriptor.ServiceType == serviceType);
        }

        // The invariant stated as one assertion rather than N cases, and the one that reads usefully in a
        // failure log: it names the drifting interfaces AND the direction of the drift, which is the fact a
        // per-case failure makes you reconstruct.
        [Fact]
        public void The_two_providers_register_exactly_the_same_closed_store_interfaces()
        {
            var ef = NewServices();
            ef.AddStommeFrameworkStore();

            var raven = NewServices();
            raven.AddRavenFrameworkStore(NewStore());

            var contracts = DiscoverClosedStoreContracts().ToHashSet();
            var efContracts = ef.Select(descriptor => descriptor.ServiceType).Where(contracts.Contains).ToHashSet();
            var ravenContracts = raven.Select(descriptor => descriptor.ServiceType).Where(contracts.Contains).ToHashSet();

            var missingFromRaven = contracts.Except(ravenContracts).Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal);
            var missingFromEf = contracts.Except(efContracts).Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal);

            Assert.True(
                efContracts.SetEquals(contracts) && ravenContracts.SetEquals(contracts),
                $"The framework store's providers have drifted apart.{Environment.NewLine}" +
                $"  Declared closed store contracts: {contracts.Count}{Environment.NewLine}" +
                $"  Registered by EF Core:           {efContracts.Count}{Environment.NewLine}" +
                $"  Registered by RavenDB:           {ravenContracts.Count}{Environment.NewLine}" +
                $"  Declared but MISSING from RavenDB: {string.Join(", ", missingFromRaven)}{Environment.NewLine}" +
                $"  Declared but MISSING from EF Core: {string.Join(", ", missingFromEf)}");
        }

        // Both halves of the Setting cluster resolve, and they are DIFFERENT implementations bound to
        // different databases. One store serving both would compile, register and silently read the
        // master's Host settings out of the current tenant's database.
        [Fact]
        public void The_two_setting_stores_are_distinct_implementations()
        {
            var services = NewServices();
            services.AddRavenFrameworkStore(NewStore(), options =>
            {
                options.FrameworkDatabase = "framework";
                options.MasterDatabase = "master";
            });

            using var provider = services.BuildServiceProvider();

            var tenant = provider.GetRequiredService<ISettingStore>();
            var master = provider.GetRequiredService<IMasterSettingStore>();

            Assert.NotEqual(tenant.GetType(), master.GetType());
        }

        private sealed class SystemScopeTenantContext : Tenancy.Abstractions.ITenantContext
        {
            public Tenancy.Domain.TenantId? CurrentTenantId => null;

            public bool IsSystemScope => true;
        }
    }
}
