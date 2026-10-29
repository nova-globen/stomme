using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Infrastructure.Security;
using Stomme.Security;
using Stomme.Shared;
using Stomme.Tests.Fixtures.GateTestModule;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The RESERVED-PREFIX half of the declaration gate. Seven modules that were dissolved into framework surfaces
    /// took their permission keys with them, and those prefixes are reserved so no later module can reuse them. Their vocabulary maps to
    /// framework gates (<c>files:*</c> to File/Folder permission rows, <c>tenants:*</c>/<c>ops:*</c> to Host,
    /// <c>modules:*</c>/<c>platform.modules:*</c> to Host/Admin on ModuleDefinitionController, <c>identity.*</c> to
    /// the User/Role policies, <c>features:*</c> to Admin/Host on FeatureController) - so a later lift that
    /// re-declares or re-requires one of them builds a SECOND, parallel gate over a surface the framework already
    /// guards. A sentence in the backlog is not a gate; this is.
    /// </summary>
    /// <remarks>
    /// Kept in its own partial file so it stays additive beside the rest of the declaration gate. Validated in
    /// both directions: the discovered tree yields zero violations, and each planted use yields at least one.
    /// </remarks>
    public partial class PermissionKeyDeclarationGateTests
    {
        /// <summary>The dissolved modules' key prefixes, matched case-insensitively against the FULL key.</summary>
        internal static readonly IReadOnlyList<string> ReservedDissolvedModulePrefixes = new[]
        {
            "files:",
            "tenants:",
            "modules:",
            "platform.modules:",
            "identity.",
            "features:",
            "ops:"
        };

        [Fact]
        public void No_declared_key_or_requirement_uses_a_reserved_dissolved_module_prefix()
        {
            var definitions = DiscoveredDefinitions();
            var requirements = DiscoveredRequirements();
            var endpoints = DiscoveredKeyGatedEndpoints();

            // Not vacuous: each of the three scanned sets reaches the fixture's real keys.
            Assert.Contains(definitions, definition => PermissionKeys.ParseKeys(definition.PermissionNames).Any(key => key.Key == GateTestPermissions.Manage));
            Assert.Contains(requirements, requirement => requirement.Permission == GateTestPermissions.Manage);
            Assert.Contains(endpoints, endpoint => endpoint.Key == GateTestPermissions.Manage);

            var violations = ValidateReservedPrefixes(
                definitions.Select(definition => (definition.Name, definition.PermissionNames)),
                requirements,
                endpoints);
            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.Empty(violations);
        }

        [Fact]
        public void The_reserved_prefix_gate_fails_a_planted_files_upload_declaration()
        {
            var planted = PermissionKeys.Declare(new[] { "files:upload" }, RoleNames.Admin);

            // The control: the declaration is otherwise WELL FORMED, so the prefix is the only thing wrong with it.
            Assert.Empty(ValidateDeclarations(new[] { ("PlantedFiles", planted) }));

            var violations = ValidateReservedPrefixes(new[] { ("PlantedFiles", planted) }, Array.Empty<Requirement>(), Array.Empty<KeyGatedEndpoint>());
            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.NotEmpty(violations);
        }

        [Fact]
        public void The_reserved_prefix_gate_fails_a_planted_requires_ops_view()
        {
            // A planted [Requires("ops:view")] owner, recorded as the discovery would record it. It is NOT a real
            // attributed type: the [Requires] discovery scans every loaded Stomme assembly with no declaring-type
            // exclusion, so a real planted attribute would turn the existing requires gate red for the whole tree.
            var requirement = new Requirement("PlantedOpsView", null, "ops:view", false);
            var declaration = ("PlantedOps", PermissionKeys.Declare(new[] { "ops:view" }, RoleNames.Admin));

            // The control: declared, well formed, Send-reachable - the requires gate itself has nothing to say.
            Assert.Empty(ValidateRequirements(new[] { requirement }, new[] { declaration }));

            var violations = ValidateReservedPrefixes(Array.Empty<(string, string)>(), new[] { requirement }, Array.Empty<KeyGatedEndpoint>());
            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.NotEmpty(violations);
        }

        [Fact]
        public void The_reserved_prefix_gate_fails_a_planted_requires_permission_key_identity_users()
        {
            var planted = DiscoverKeyGatedEndpoints(new[] { typeof(PlantedIdentityUsersController) });

            // The control: discovery found the gated action, and it is not anonymous - the prefix is the only fault.
            Assert.NotEmpty(planted);
            Assert.Empty(ValidateKeyGatedEndpoints(planted));

            var violations = ValidateReservedPrefixes(Array.Empty<(string, string)>(), Array.Empty<Requirement>(), planted);
            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.NotEmpty(violations);
        }

        [Fact]
        public void The_reserved_prefix_gate_passes_a_look_alike_key_outside_the_reserved_set()
        {
            // The control for the control: the matcher is a PREFIX on the full key, not a substring search.
            var violations = ValidateReservedPrefixes(
                new[] { ("PlantedLookAlike", PermissionKeys.Declare(new[] { "profiles:view", "myfiles:upload", "platform:manage" }, RoleNames.Admin)) },
                new[] { new Requirement("PlantedSystemAdmin", null, "system:admin", false), new Requirement("PlantedFeatureOnly", "features", null, false) },
                Array.Empty<KeyGatedEndpoint>());

            Assert.Empty(violations);
        }

        /// <summary>
        /// The reserved-prefix rule over declarations, <c>[Requires]</c> and <c>[RequiresPermissionKey]</c>.
        /// Returns one line per violation; an empty list is a pass.
        /// </summary>
        internal static List<string> ValidateReservedPrefixes(
            IEnumerable<(string Name, string PermissionNames)> definitions,
            IEnumerable<Requirement> requirements,
            IEnumerable<KeyGatedEndpoint> endpoints)
        {
            var violations = new List<string>();

            foreach (var (name, permissionNames) in definitions)
            {
                foreach (var key in PermissionKeys.ParseKeys(permissionNames))
                {
                    if (ReservedPrefixOf(key.Key) is { } prefix)
                    {
                        violations.Add($"{name}: declares '{key.Key}' under the reserved prefix '{prefix}' of a module that DISSOLVED into a framework gate - map it to that gate instead of declaring a parallel key.");
                    }
                }
            }

            foreach (var requirement in requirements)
            {
                if (ReservedPrefixOf(requirement.Permission) is { } prefix)
                {
                    violations.Add($"{requirement.Owner}: [Requires(\"{requirement.Permission}\")] uses the reserved prefix '{prefix}' of a module that DISSOLVED into a framework gate.");
                }
            }

            foreach (var endpoint in endpoints)
            {
                if (ReservedPrefixOf(endpoint.Key) is { } prefix)
                {
                    violations.Add($"{endpoint.Owner}: [RequiresPermissionKey(\"{endpoint.Key}\")] uses the reserved prefix '{prefix}' of a module that DISSOLVED into a framework gate.");
                }
            }

            return violations;
        }

        private static string ReservedPrefixOf(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var trimmed = key.Trim();
            return ReservedDissolvedModulePrefixes.FirstOrDefault(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // A private NESTED planted type, excluded from the real endpoint scan by its declaring type (the same
        // construction as the anonymous-controller fixtures).
        private sealed class PlantedIdentityUsersController
        {
            [RequiresPermissionKey("identity.users:view")]
            public string Users() => "";
        }
    }
}