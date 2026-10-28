using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Metering;
using Stomme.Models;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Controllers;
using Stomme.Modules.Metering.Domain;
using Stomme.Modules.Metering.Infrastructure;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The reflective gates for the metering module (D102): what it declares, what it may depend on, what kind of
    /// hosted service it is allowed to be, and that it is OFF until an operator says otherwise.
    /// </summary>
    /// <remarks>
    /// Each of these fails the build on a class of mistake that compiles and renders: a request gated with a key the
    /// module never declared (so no row exists and the bridge denies for ever, or - worse - a bare two-segment key
    /// that <c>PermissionKeys.Declare</c> silently ignores); a type outside the module reaching into its domain,
    /// which is what makes the port swappable; a second <c>IHostedService</c> that the assembly scan would register
    /// as a singleton and run once for whichever tenant happened to be ambient - the shape the predecessor platform's QuotaSeeder had.
    /// </remarks>
    public class MeteringShapeGateTests
    {
        private readonly ITestOutputHelper _output;

        public MeteringShapeGateTests(ITestOutputHelper output) => _output = output;

        private static Assembly ServerAssembly => typeof(UsageRecorder).Assembly;

        private static Assembly ClientAssembly => typeof(Modules.Metering.ModuleInfo).Assembly;

        [Fact]
        public void Every_requires_key_on_a_metering_request_is_one_the_module_declares()
        {
            var declared = new HashSet<string>(MeteringPermissions.All, StringComparer.Ordinal);
            var definition = new Modules.Metering.ModuleInfo().ModuleDefinition;
            _output.WriteLine($"[declared] {definition.PermissionNames}");

            // The declaration string is built by PermissionKeys.Declare, so each key must appear in it verbatim.
            foreach (var key in declared)
            {
                Assert.Contains(key, definition.PermissionNames);
            }

            var gated = MeteringRequestTypes()
                .Select(type => (Type: type, Attribute: type.GetCustomAttribute<RequiresAttribute>()))
                .ToList();

            Assert.NotEmpty(gated);
            foreach (var (type, attribute) in gated)
            {
                Assert.True(attribute is not null, $"{type.Name} is a metering request with no [Requires] - it would be reachable ungated.");
                foreach (var key in Keys(attribute))
                {
                    Assert.True(declared.Contains(key), $"{type.Name} requires '{key}', which the module never declares.");
                }
            }

            _output.WriteLine($"[gated requests] {gated.Count}");
        }

        [Fact]
        public void Every_controller_action_carries_a_declared_key_and_the_controller_is_never_anonymous()
        {
            var declared = new HashSet<string>(MeteringPermissions.All, StringComparer.Ordinal);
            var controller = typeof(MeteringController);

            Assert.Null(controller.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());

            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes().Any(attribute => attribute is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
                .ToList();

            Assert.NotEmpty(actions);
            foreach (var action in actions)
            {
                var key = action.GetCustomAttribute<RequiresPermissionKeyAttribute>();
                Assert.True(key is not null, $"MeteringController.{action.Name} has no [RequiresPermissionKey].");
                Assert.True(declared.Contains(key.Key), $"MeteringController.{action.Name} requires '{key.Key}', which the module never declares.");

                // The policy is what the middleware actually resolves; a Key and a Policy that disagreed would read
                // as gated and be decided by something else.
                Assert.Equal(RequiresPermissionKeyAttribute.PolicyName(key.Key), key.Policy);
            }

            _output.WriteLine($"[gated actions] {actions.Count}");
        }

        [Fact]
        public void Nothing_outside_the_module_depends_on_the_metering_domain()
        {
            var offenders = ReferencesFromOutsideTheModule("Stomme.Modules.Metering.Domain");
            _output.WriteLine(offenders.Count == 0 ? "[clean]" : string.Join(Environment.NewLine, offenders));
            Assert.Empty(offenders);
        }

        /// <summary>
        /// The vacuity guard for the scan above, and it is not decorative.
        /// </summary>
        /// <remarks>
        /// MEASURED while writing this file: the first version of the scan was pointed at the PORT namespace
        /// (<c>Stomme.Metering</c>) expecting it to light up, and it reported CLEAN - because every consumer of the
        /// port happens to live under <c>Stomme.Modules.Metering.*</c>, which the scan excludes. A scan that
        /// cannot fail is not a gate, so this test proves the mechanism can see a cross-namespace reference at all
        /// by pointing it at one that certainly exists.
        /// </remarks>
        [Fact]
        public void The_dependency_scan_can_actually_see_a_reference()
        {
            var known = ReferencesFromOutsideTheModule("Stomme.Shared");
            _output.WriteLine($"[known-dirty] {known.Count} reference(s) to Stomme.Shared from outside the module");
            Assert.NotEmpty(known);
        }

        /// <summary>
        /// The module's own namespaces. <c>Stomme.Metering.Persistence</c> is one of them despite the name: the
        /// repository contracts live OUTSIDE <c>Stomme.Modules.*</c> deliberately, because
        /// <c>ModuleStoreRegistrationTests</c> sweeps that namespace for closed store contracts and then requires
        /// HtmlText's SEAM extension to register each one - which a clean-arch module's provider-neutral repository
        /// can never satisfy. The sibling Notifications module solved it the same way, and the full gate is what
        /// found this: four of its theories went red naming the metering contracts.
        /// </summary>
        private static readonly string[] ModuleNamespaces =
        [
            "Stomme.Modules.Metering",
            "Stomme.Metering.Persistence",
        ];

        private static List<string> ReferencesFromOutsideTheModule(string namespacePrefix)
        {
            var offenders = new List<string>();

            foreach (var assembly in new[] { ServerAssembly, ClientAssembly })
            {
                foreach (var type in Loadable(assembly).Where(type => !ModuleNamespaces.Any(module => (type.Namespace ?? string.Empty).StartsWith(module, StringComparison.Ordinal))))
                {
                    foreach (var referenced in Referenced(type))
                    {
                        if ((referenced.Namespace ?? string.Empty).StartsWith(namespacePrefix, StringComparison.Ordinal))
                        {
                            offenders.Add($"{type.FullName} -> {referenced.FullName}");
                        }
                    }
                }
            }

            return offenders;
        }

        [Fact]
        public void The_module_ships_exactly_two_jobs_and_no_other_hosted_service()
        {
            var hosted = Loadable(ServerAssembly)
                .Where(type => (type.Namespace ?? string.Empty).StartsWith("Stomme.Modules.Metering", StringComparison.Ordinal))
                .Where(type => typeof(IHostedService).IsAssignableFrom(type) && !type.IsAbstract)
                .ToList();

            _output.WriteLine($"[hosted] {string.Join(", ", hosted.Select(type => type.Name))}");

            Assert.Equal(2, hosted.Count);
            Assert.Contains(typeof(UsageRollupJob), hosted);
            Assert.Contains(typeof(MeteringFoldRecoveryJob), hosted);
            foreach (var type in hosted)
            {
                Assert.True(typeof(HostedServiceBase).IsAssignableFrom(type),
                    $"{type.Name} is an IHostedService that is not a HostedServiceBase: the assembly scan would register it as a singleton and start it once, for whichever tenant happened to be ambient.");
            }
        }

        [Theory]
        [InlineData("", false)]
        [InlineData("no", false)]
        [InlineData("False", false)]
        [InlineData("true", true)]
        [InlineData("True", true)]
        public void Metering_is_off_unless_configuration_definitely_says_otherwise(string value, bool expected)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { [MeteringSwitch.EnabledKey] = value })
                .Build();

            Assert.Equal(expected, MeteringSwitch.IsEnabled(configuration));
        }

        /// <summary>
        /// Pins the conversion an instant read back from the store goes through - and does so without depending on
        /// the machine's time zone.
        /// </summary>
        /// <remarks>
        /// The defect this guards was found by driving the rollup pass on a UTC+1 machine: two AI calls an hour
        /// apart landed on two different DAYS, because the pass snapped the day with <c>ToUniversalTime()</c> and
        /// the relational provider returns <see cref="DateTimeKind.Unspecified"/>, so the conversion subtracted the
        /// machine's offset and pushed the earlier call across midnight. On a UTC runner - which is what CI is -
        /// that bug is completely invisible, so the behavioural test cannot be the only cover: this one asserts the
        /// TICKS are untouched, which is true in every zone.
        /// </remarks>
        [Fact]
        public void An_instant_read_back_from_the_store_is_taken_as_utc_and_never_shifted()
        {
            var stored = new DateTime(2026, 9, 19, 1, 0, 0, DateTimeKind.Unspecified);

            var asUtc = MeteringTime.AsUtc(stored);
            Assert.Equal(DateTimeKind.Utc, asUtc.Kind);
            Assert.Equal(stored.Ticks, asUtc.Ticks);
            Assert.Equal(new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc), MeteringTime.DayStart(stored));

            // A UTC instant is returned untouched, and a local one IS converted - the two cases the assumption
            // above must not swallow.
            var utc = new DateTime(2026, 9, 19, 1, 0, 0, DateTimeKind.Utc);
            Assert.Equal(utc, MeteringTime.AsUtc(utc));

            var local = new DateTime(2026, 9, 19, 1, 0, 0, DateTimeKind.Local);
            Assert.Equal(local.ToUniversalTime().Ticks, MeteringTime.AsUtc(local).Ticks);

            _output.WriteLine($"[time] stored {stored:O} -> {asUtc:O}; local offset here is {TimeZoneInfo.Local.GetUtcOffset(stored)}");
        }

        [Fact]
        public void Metering_is_off_when_there_is_no_configuration_at_all()
        {
            Assert.False(MeteringSwitch.IsEnabled(null));
            Assert.False(MeteringSwitch.IsEnabled(new ConfigurationBuilder().Build()));
        }

        private static IEnumerable<Type> MeteringRequestTypes() =>
            Loadable(ServerAssembly)
                .Where(type => type.Namespace == "Stomme.Modules.Metering.Application")
                .Where(type => !type.IsAbstract && type.GetInterfaces().Any(contract =>
                    contract.IsGenericType
                    && (contract.GetGenericTypeDefinition().Name.StartsWith("IQuery", StringComparison.Ordinal)
                        || contract.GetGenericTypeDefinition().Name.StartsWith("ICommand", StringComparison.Ordinal))
                    && !contract.GetGenericTypeDefinition().Name.Contains("Handler", StringComparison.Ordinal)));

        private static IEnumerable<string> Keys(Attribute attribute)
        {
            if (attribute is null)
            {
                yield break;
            }

            // Both attributes carry their keys as a string or a string[] property; read whichever is present so the
            // gate does not depend on the shape of either attribute.
            foreach (var property in attribute.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var value = property.GetValue(attribute);
                if (value is string single && single.Contains(':'))
                {
                    yield return single;
                }
                else if (value is IEnumerable<string> many)
                {
                    foreach (var item in many.Where(item => item is not null && item.Contains(':')))
                    {
                        yield return item;
                    }
                }
            }
        }

        private static IEnumerable<Type> Referenced(Type type)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                yield return field.FieldType;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                yield return property.PropertyType;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                yield return method.ReturnType;
                foreach (var parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }
        }

        private static IEnumerable<Type> Loadable(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type is not null);
            }
        }
    }
}
