#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Mediator;
using Stomme.Security.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The request-authorization coverage check: every mediator REQUEST type in the three modules'
    /// application layers (Formats, Connectivity, Datasets) carries a <c>[Requires]</c> naming a permission, because the
    /// mediator's AuthorizationBehavior decides on the request type and nothing else does - a request without one is an
    /// ungated door from every controller, service or MCP tool that sends it. Validated both ways: the clean tree passes,
    /// a planted ungated request is caught by the same scanner.
    /// </summary>
    public sealed class ModuleRequestAuthorizationCoverageTests(ITestOutputHelper output)
    {
        private static readonly string[] Namespaces =
        {
            "Stomme.Modules.Formats.Application",
            "Stomme.Modules.Connectivity.Application",
            "Stomme.Modules.Datasets.Application",
        };

        [Fact]
        public void Every_wave_2_mediator_request_carries_a_permission_requirement()
        {
            var requests = typeof(Stomme.Modules.Connectivity.Application.CreateConnectionCommand).Assembly.GetTypes()
                .Where(type => type.Namespace is { } ns && Namespaces.Contains(ns, StringComparer.Ordinal) && IsRequest(type))
                .ToList();
            foreach (var request in requests)
            {
                output.WriteLine(request.FullName);
            }

            Assert.True(requests.Count >= 10, $"vacuous: only {requests.Count} Wave 2 request types found");
            Assert.Empty(Ungated(requests));
        }

        // The Pipelines module too. Found by a mutation check on its Files page - the listing query's
        // [Requires] removed, every test still green: an Interactive Server page sends the request to the mediator directly, so
        // the controller's key never sees it and the request's own requirement is the only door.
        private static readonly string[] PipelinesNamespaces =
        {
            "Stomme.Modules.Pipelines.Application",
            "Stomme.Modules.Pipelines.Comments",
        };

        [Fact]
        public void Every_pipelines_mediator_request_carries_a_permission_requirement()
        {
            var requests = typeof(Stomme.Modules.Pipelines.Application.ListPipelineFilesQuery).Assembly.GetTypes()
                .Where(type => type.Namespace is { } ns && PipelinesNamespaces.Contains(ns, StringComparer.Ordinal) && IsRequest(type))
                .ToList();
            foreach (var request in requests)
            {
                output.WriteLine(request.FullName);
            }

            Assert.True(requests.Count >= 60, $"vacuous: only {requests.Count} Pipelines request types found");
            Assert.Empty(Ungated(requests));
        }

        [Fact]
        public void The_scanner_catches_a_planted_ungated_request()
        {
            // The planted type carries NO attribute on purpose: PermissionKeyDeclarationGateTests scans every loaded assembly,
            // this one included, and a planted [Requires] would be judged as a real one. A real gated request is the control.
            var ungated = Ungated(new[] { typeof(PlantedUngatedRequest), typeof(Stomme.Modules.Connectivity.Application.CreateConnectionCommand) });
            Assert.Equal(new[] { typeof(PlantedUngatedRequest).FullName }, ungated);
        }

        private static bool IsRequest(Type type) =>
            !type.IsAbstract && type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRequest<>));

        private static IReadOnlyList<string?> Ungated(IEnumerable<Type> types) =>
            types.Where(type => !type.GetCustomAttributes<RequiresAttribute>(inherit: false).Any(requires => !string.IsNullOrWhiteSpace(requires.Permission)))
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

        private sealed record PlantedUngatedRequest : IRequest<int>;
    }
}
