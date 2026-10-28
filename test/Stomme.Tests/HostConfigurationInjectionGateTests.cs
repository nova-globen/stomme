using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-251 / BL-162's class, as a gate: no constructor in the host's assembly takes the plain <see cref="IConfiguration"/>.
    /// In this host the container's <see cref="IConfiguration"/> is the web builder's default, WITHOUT
    /// <c>appsettings.override.json</c> - where an install keeps every setting it writes - so a class that injected it ignored
    /// what the operator set: the MCP client's host allowlist, the SAML and Windows sign-in claim types (found by the
    /// app-guidance writers). Ask for <see cref="IConfigurationRoot"/>, or resolve through <c>HostConfiguration.Resolve</c>.
    /// </summary>
    public sealed class HostConfigurationInjectionGateTests
    {
        // Each entry says why its IConfiguration is the host's own; an entry nothing matches any more fails the gate.
        private static readonly string[] Allowed =
        {
            // Built only by its factory in the Metering startup, which passes HostConfiguration.Resolve(sp) - the layered root.
            "Stomme.Modules.Metering.Application.AiCostCalculator",
        };

        [Fact]
        public void No_constructor_in_the_host_takes_the_plain_IConfiguration()
        {
            var host = typeof(Stomme.Infrastructure.HostConfiguration).Assembly;
            var types = host.GetTypes();
            Assert.True(types.Length > 1000, $"only {types.Length} types scanned - the scan is not looking at the host"); // vacuity floor

            var offenders = types
                .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IConfiguration)))
                    .Select(_ => type.FullName))
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var unexplained = offenders.Except(Allowed).ToList();
            Assert.True(unexplained.Count == 0, "These take the plain IConfiguration, which misses the override file - take IConfigurationRoot: " + string.Join(", ", unexplained));
            var stale = Allowed.Except(offenders).ToList();
            Assert.True(stale.Count == 0, "Allowed entries that no longer take IConfiguration - remove them: " + string.Join(", ", stale));
        }
    }
}
