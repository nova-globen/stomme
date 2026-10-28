using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Modules.HtmlText.Repository;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// D55 — the MODULE-scoped twin of <see cref="RavenFrameworkStoreRegistrationTests"/> (orchestrator decision 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework gate discovers its contracts from <c>typeof(ISiteStore).Namespace</c> —
    /// <c>Stomme.Repository.Stores</c> — so a module store in <c>Stomme.Modules.HtmlText.Repository</c> is
    /// INVISIBLE to it, one namespace out, and its RavenDB twin would be UNGATED: the exact drift D42 exists to
    /// prevent. This gate closes that gap for modules. It discovers every closed store contract declared under
    /// <c>Stomme.Modules.*</c> reflectively — no hand-maintained list — and holds BOTH providers to it, exactly
    /// as D42 holds the framework's two providers to the framework contract set.
    /// </para>
    /// <para>
    /// It is a SEPARATE gate rather than an extension of the framework discovery, because the framework theories
    /// call <c>AddStommeFrameworkStore</c>/<c>AddRavenFrameworkStore</c>, which do not (and must not) register
    /// module contracts. A module registers its own twins through its own two extensions; this gate exercises
    /// those.
    /// </para>
    /// <para>
    /// VALIDATED BOTH DIRECTIONS (D55a report): green with both twins present, and red when the Raven registration
    /// of <see cref="IHtmlTextStore"/> is removed (the Raven theory fails on that contract) — the same fire the
    /// missing <c>RavenPermissionStore</c> would have drawn had D42 covered modules.
    /// </para>
    /// </remarks>
    public class ModuleStoreRegistrationTests
    {
        public static TheoryData<Type> ModuleStoreContracts()
        {
            var contracts = new TheoryData<Type>();
            foreach (var contract in DiscoverModuleStoreContracts())
            {
                contracts.Add(contract);
            }

            return contracts;
        }

        // Every closed store contract declared under Stomme.Modules.* in the server assembly: a public interface
        // that either closes the seam's open-generic IRepository<,> over one aggregate or is one of the module's
        // own read-path session factories (IDocumentSessionFactory descendants). The open generics themselves are
        // excluded — registering them is the very thing D17 rule 1 forbids.
        private static IEnumerable<Type> DiscoverModuleStoreContracts() =>
            typeof(IHtmlTextStore).Assembly
                .GetTypes()
                .Where(type => type.IsInterface && type.IsPublic)
                .Where(type => type.Namespace != null
                    && type.Namespace.StartsWith("Stomme.Modules.", StringComparison.Ordinal))
                .Where(IsClosedStoreContract)
                .OrderBy(type => type.Name, StringComparer.Ordinal);

        private static bool IsClosedStoreContract(Type type)
        {
            if (type == typeof(IDocumentSessionFactory) || type == typeof(IRepository<,>))
            {
                return false;
            }

            if (typeof(IDocumentSessionFactory).IsAssignableFrom(type))
            {
                return true;
            }

            return type.GetInterfaces().Any(implemented =>
                implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(IRepository<,>));
        }

        // The discovery must not silently find nothing (the D42 vacuity guard, module-scoped): a rule that matched
        // zero types would make the theories below vacuous and the gate would pass by running no cases. Asserted
        // against a floor plus the two contracts HtmlText is known to declare.
        [Fact]
        public void Discovery_finds_the_module_store_contracts()
        {
            var contracts = DiscoverModuleStoreContracts().ToList();

            Assert.Contains(typeof(IHtmlTextStore), contracts);
            Assert.Contains(typeof(IHtmlTextStoreSessionFactory), contracts);
            Assert.DoesNotContain(typeof(IRepository<,>), contracts);
            Assert.True(contracts.Count >= 2,
                $"Discovery found only {contracts.Count} module store contract(s); HtmlText alone declares 2.");
        }

        // THE GATE (EF half). Every module closed store contract is registered by the module's EF extension.
        [Theory]
        [MemberData(nameof(ModuleStoreContracts))]
        public void The_ef_module_store_registers_every_closed_store_interface(Type serviceType)
        {
            var services = new ServiceCollection();

            services.AddHtmlTextEfStore();

            Assert.Contains(services, descriptor => descriptor.ServiceType == serviceType);
        }

        // THE GATE (RavenDB half). The mirror — a module store added to EF but not RavenDB is the drift D42 exists
        // to stop, and it fails here. This is the theory that goes red if the RavenDB twin's registration is removed.
        [Theory]
        [MemberData(nameof(ModuleStoreContracts))]
        public void The_raven_module_store_registers_every_closed_store_interface(Type serviceType)
        {
            var services = new ServiceCollection();

            services.AddHtmlTextRavenStore();

            Assert.Contains(services, descriptor => descriptor.ServiceType == serviceType);
        }

        // The invariant as one assertion that reads usefully in a failure log: it names the drifting interfaces AND
        // the direction of the drift.
        [Fact]
        public void The_two_providers_register_exactly_the_same_module_store_interfaces()
        {
            var ef = new ServiceCollection();
            ef.AddHtmlTextEfStore();

            var raven = new ServiceCollection();
            raven.AddHtmlTextRavenStore();

            var contracts = DiscoverModuleStoreContracts().ToHashSet();
            var efContracts = ef.Select(descriptor => descriptor.ServiceType).Where(contracts.Contains).ToHashSet();
            var ravenContracts = raven.Select(descriptor => descriptor.ServiceType).Where(contracts.Contains).ToHashSet();

            var missingFromRaven = contracts.Except(ravenContracts).Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal);
            var missingFromEf = contracts.Except(efContracts).Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal);

            Assert.True(
                efContracts.SetEquals(contracts) && ravenContracts.SetEquals(contracts),
                $"A module's store providers have drifted apart.{Environment.NewLine}" +
                $"  Declared closed module store contracts: {contracts.Count}{Environment.NewLine}" +
                $"  Registered by EF Core:                  {efContracts.Count}{Environment.NewLine}" +
                $"  Registered by RavenDB:                  {ravenContracts.Count}{Environment.NewLine}" +
                $"  Declared but MISSING from RavenDB: {string.Join(", ", missingFromRaven)}{Environment.NewLine}" +
                $"  Declared but MISSING from EF Core: {string.Join(", ", missingFromEf)}");
        }
    }
}
