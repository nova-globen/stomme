#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-94 increment 3, the two gates around the contract. A type key published twice refuses the COMPOSITION:
    /// the catalog throws when the host first resolves it, naming the key and both owners (in both directions: a planted
    /// duplicate throws, distinct keys in different cases resolve). And the contract is BCL-only: every type in
    /// <c>Stomme.Shared</c>'s <c>Stomme.Connectivity</c> namespace references, through its bases, interfaces, members and
    /// parameters, only the BCL and that namespace itself - so the connectors of BL-96 reference <c>Stomme.Shared</c> and
    /// nothing else; a planted reference to a server type is caught by the same walk.
    /// </summary>
    public class ConnectorCatalogGateTests(ITestOutputHelper output)
    {
        [Fact]
        public void A_planted_duplicate_key_refuses_the_composition_and_names_both_owners()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConnector>(new FixtureConnector("rest", "First"));
            services.AddSingleton<IConnector>(new FixtureExtractingConnector("Rest", "Second"));
            services.AddConnectorCatalog();
            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IConnectorCatalog>());

            Assert.Contains("'Rest'", ex.Message);
            Assert.Contains(nameof(FixtureConnector), ex.Message);
            Assert.Contains(nameof(FixtureExtractingConnector), ex.Message);
        }

        [Fact]
        public void Distinct_keys_compose_and_resolve_in_any_case()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConnector>(new FixtureConnector("rest", "REST"));
            services.AddSingleton<IConnector>(new FixtureConnector("postgresql", "PostgreSQL"));
            services.AddConnectorCatalog();
            using var provider = services.BuildServiceProvider();

            var catalog = provider.GetRequiredService<IConnectorCatalog>();

            Assert.Equal(2, catalog.All.Count);
            Assert.Equal("PostgreSQL", catalog.Find("POSTGRESQL")!.DisplayName);
            Assert.Equal("REST", catalog.Find(" rest ")!.DisplayName);
        }

        [Fact]
        public void The_shared_contract_references_the_BCL_only()
        {
            var shared = typeof(IConnector).Assembly;
            var contract = shared.GetTypes().Where(type => type.Namespace == typeof(IConnector).Namespace).ToList();
            Assert.True(contract.Count >= 15, "the contract's types must be discovered; found " + contract.Count);

            var offenders = Offenders(contract);
            foreach (var type in contract)
            {
                output.WriteLine(type.Name);
            }

            Assert.True(offenders.Count == 0, "Contract types referencing something beyond the BCL: " + string.Join("; ", offenders));
        }

        [Fact]
        public void The_dependency_walk_catches_a_planted_server_reference()
        {
            var offenders = Offenders(new[] { typeof(PlantedContractType) });

            Assert.Contains(offenders, offender => offender.Contains(nameof(PlantedContractType), StringComparison.Ordinal) && offender.Contains("ConnectorCatalog", StringComparison.Ordinal));
        }

        /// <summary>A type that would be a contract type, referencing a SERVER type through a member - what the gate must catch.</summary>
        public sealed class PlantedContractType
        {
            public ConnectorCatalog? Catalog { get; set; }
        }

        private static List<string> Offenders(IEnumerable<Type> contract)
        {
            var shared = typeof(IConnector).Assembly;
            var offenders = new List<string>();
            foreach (var type in contract)
            {
                foreach (var referenced in ReferencedTypes(type))
                {
                    if (referenced.Assembly == shared || IsBcl(referenced.Assembly))
                    {
                        continue;
                    }

                    offenders.Add($"{type.FullName} -> {referenced.FullName} ({referenced.Assembly.GetName().Name})");
                }
            }

            return offenders.Distinct(StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<Type> ReferencedTypes(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var seen = new HashSet<Type>();
            void Add(Type? candidate)
            {
                if (candidate is null)
                {
                    return;
                }

                if (candidate.IsGenericType && !candidate.IsGenericTypeDefinition)
                {
                    foreach (var argument in candidate.GetGenericArguments())
                    {
                        Add(argument);
                    }

                    candidate = candidate.GetGenericTypeDefinition();
                }

                if (candidate.IsArray || candidate.IsByRef || candidate.IsPointer)
                {
                    Add(candidate.GetElementType());
                    return;
                }

                if (!candidate.IsGenericParameter)
                {
                    seen.Add(candidate);
                }
            }

            Add(type.BaseType);
            foreach (var iface in type.GetInterfaces())
            {
                Add(iface);
            }

            foreach (var property in type.GetProperties(all))
            {
                Add(property.PropertyType);
            }

            foreach (var field in type.GetFields(all))
            {
                Add(field.FieldType);
            }

            foreach (var method in type.GetMethods(all))
            {
                Add(method.ReturnType);
                foreach (var parameter in method.GetParameters())
                {
                    Add(parameter.ParameterType);
                }
            }

            foreach (var constructor in type.GetConstructors(all))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    Add(parameter.ParameterType);
                }
            }

            return seen;
        }

        private static bool IsBcl(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? string.Empty;
            return name == "System.Private.CoreLib" || name == "netstandard" || name == "mscorlib" || name.StartsWith("System.", StringComparison.Ordinal) || name == "System";
        }
    }
}
