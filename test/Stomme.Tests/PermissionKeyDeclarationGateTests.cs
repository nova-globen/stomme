using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tests.Fixtures.GateTestModule;
using Microsoft.AspNetCore.Authorization;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The REFLECTIVE declaration gate of the authorization bridge (D99, BL-99): every permission KEY a module
    /// declares is well formed, and every <c>[Requires]</c> in a loaded module assembly names a key some module
    /// actually declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a gate and not a convention.</b> The framework SILENTLY IGNORES a malformed declaration (F2,
    /// measured): a bare two-segment key renders no grid column, materialises no Permission row, and is written
    /// by the control-panel add-module path as a literal Module permission - and a key whose casing differs from
    /// the row it is looked up against is a silent denial on PostgreSQL and a silent grant on RavenDB (D49).
    /// Every failure mode here is invisible at runtime, which is exactly what a gate is for.
    /// </para>
    /// <para>
    /// <b>The discriminator, and what it is NOT.</b> Stomme's <c>PermissionNames</c> string has carried the
    /// same 3-segment <c>EntityName:Permission:Roles</c> form since long before this bridge - the stock Users,
    /// Roles and Profiles modules declare <c>User:Write:Administrators</c> and friends. Those are FRAMEWORK API
    /// POLICIES, not lifted keys: they are TitleCase, they name <see cref="EntityNames"/> constants, and
    /// <c>UserRole</c> is deliberately declared by TWO modules. A gate that demanded lower-case and a unique
    /// resource of every 3-segment entry would fail on master's own code. The rule is therefore: an entry whose
    /// resource is an <see cref="EntityNames"/> constant is a framework policy (it must match the constant
    /// EXACTLY); every other 3-segment entry is a lifted KEY and must satisfy the key contract.
    /// </para>
    /// <para>
    /// Validated in BOTH directions: the real, discovered set must produce ZERO violations, and each planted bad
    /// declaration/attribute must produce at least one - an instrument that cannot fail proves nothing.
    /// </para>
    /// </remarks>
    public partial class PermissionKeyDeclarationGateTests
    {
        private readonly ITestOutputHelper _output;

        public PermissionKeyDeclarationGateTests(ITestOutputHelper output) => _output = output;

        // ---- the real, discovered set ---------------------------------------------------------------------

        [Fact]
        public void Every_discovered_module_definition_declares_well_formed_permission_names()
        {
            var definitions = DiscoveredDefinitions();

            // Not vacuous: the stock framework modules are in the set (they carry the framework API policies),
            // and so is the BL-99 fixture, which declares real keys.
            Assert.True(definitions.Count > 5, $"only {definitions.Count} module definitions were discovered");
            Assert.Contains(definitions, definition => PermissionKeys.ParseKeys(definition.PermissionNames).Count > 0);
            Assert.Contains(definitions, definition => PermissionKeys.ParseEntries(definition.PermissionNames)
                .Any(entry => PermissionKeys.IsFrameworkEntity(entry.Resource)));

            var violations = ValidateDeclarations(definitions.Select(definition => (definition.Name, definition.PermissionNames)));
            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.Empty(violations);
        }

        [Fact]
        public void Every_requires_attribute_in_a_loaded_module_assembly_names_a_declared_key()
        {
            var attributes = DiscoveredRequirements();
            var definitions = DiscoveredDefinitions();

            // Not vacuous: the BL-99 fixture command carries [Requires("gatetest:manage")].
            Assert.Contains(attributes, requirement => requirement.Permission == GateTestPermissions.Manage);

            var violations = ValidateRequirements(attributes, definitions.Select(definition => (definition.Name, definition.PermissionNames)));
            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.Empty(violations);
        }

        // ---- the other direction: the instrument must fail on known-dirty input ---------------------------

        public static TheoryData<string, string> BadDeclarations() => new()
        {
            { "uppercase key", "View,Edit,GateTest:Manage:Administrators" },
            { "bare two-segment key (F2: silently ignored)", "View,Edit,gatetest:manage" },
            { "View,Edit not first", "gatetest:manage:Administrators,View,Edit" },
            { "EntityNames collision", "View,Edit,user:write:Administrators" },
            { "resource over the column bound", "View,Edit," + new string('g', PermissionKeys.MaxSegmentLength + 1) + ":manage:Administrators" },
            { "too many segments", "View,Edit,gatetest:manage:extra:Administrators" },
            { "empty operation", "View,Edit,gatetest::Administrators" },
            { "framework policy with the wrong casing", "View,Edit,USER:Write:Administrators" }
        };

        [Theory]
        [MemberData(nameof(BadDeclarations))]
        public void The_declaration_gate_fails_a_planted_bad_declaration(string why, string permissionNames)
        {
            var violations = ValidateDeclarations(new[] { ("Planted", permissionNames) });

            foreach (var violation in violations)
            {
                _output.WriteLine($"[{why}] {violation}");
            }

            Assert.NotEmpty(violations);
        }

        [Fact]
        public void The_declaration_gate_fails_a_resource_declared_by_two_definitions()
        {
            var violations = ValidateDeclarations(new[]
            {
                ("PlantedOne", "View,Edit,gatetest:manage:Administrators"),
                ("PlantedTwo", "View,Edit,gatetest:view:Administrators")
            });

            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.NotEmpty(violations);
        }

        [Fact]
        public void The_declaration_gate_passes_a_planted_good_declaration()
        {
            // The control for the control: the validator is not simply returning a violation for everything.
            var violations = ValidateDeclarations(new[]
            {
                ("PlantedGood", PermissionKeys.Declare(new[] { "planted:view", "planted:manage" }, RoleNames.Admin)),
                ("PlantedFrameworkPolicy", $"{PermissionNames.View},{PermissionNames.Edit},{EntityNames.User}:{PermissionNames.Write}:{RoleNames.Admin}")
            });

            Assert.Empty(violations);
        }

        public static TheoryData<string, string, string, string, bool> BadRequirements() => new()
        {
            // why, owner, feature, permission, isNonSendContract
            { "empty [Requires()]", "PlantedEmpty", null, null, false },
            { "undeclared key", "PlantedUndeclared", null, "nosuchmodule:manage", false },
            { "[Requires] on a stream request / notification (F15: behaviors run only on Send)", "PlantedStream", null, GateTestPermissions.Manage, true },
            { "malformed key", "PlantedMalformed", null, "nocolon", false }
        };

        [Theory]
        [MemberData(nameof(BadRequirements))]
        public void The_requires_gate_fails_a_planted_bad_attribute(string why, string owner, string feature, string permission, bool isNonSendContract)
        {
            var violations = ValidateRequirements(
                new[] { new Requirement(owner, feature, permission, isNonSendContract) },
                new[] { ("PlantedGood", PermissionKeys.Declare(GateTestPermissions.All, RoleNames.Admin)) });

            foreach (var violation in violations)
            {
                _output.WriteLine($"[{why}] {violation}");
            }

            Assert.NotEmpty(violations);
        }

        [Fact]
        public void The_requires_gate_passes_a_declared_key_and_a_feature_only_requirement()
        {
            var violations = ValidateRequirements(
                new[]
                {
                    new Requirement("PlantedOk", null, GateTestPermissions.Manage, false),
                    new Requirement("PlantedFeatureOnly", "SomeFeature", null, false),
                    new Requirement("PlantedSystemAdmin", null, "system:admin", false)
                },
                new[] { ("PlantedGood", PermissionKeys.Declare(GateTestPermissions.All, RoleNames.Admin)) });

            Assert.Empty(violations);
        }

        // ---- the vocabulary helpers themselves ------------------------------------------------------------

        [Fact]
        public void Declare_puts_View_and_Edit_first_lower_cases_the_key_and_always_grants_administrators()
        {
            var declaration = PermissionKeys.Declare(new[] { "Pipelines:View", "pipelines:manage" }, "Editors");

            Assert.Equal("View,Edit,pipelines:view:Editors;Administrators,pipelines:manage:Editors;Administrators", declaration);
        }

        [Fact]
        public void Declare_rejects_a_key_that_names_a_framework_entity_or_is_malformed()
        {
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { "user:write" }));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { "nocolon" }));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { "a:b:c" }));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { new string('g', 51) + ":manage" }));
        }

        // BL-78 (D100 point 6): notifications declares DIFFERENT default roles per key (view-own for Registered
        // Users, the rest for Administrators only) - the uniform overload applies one role set to every key (F12).

        [Fact]
        public void Declare_with_per_key_roles_puts_View_and_Edit_first_and_appends_administrators_to_each_entry()
        {
            var declaration = PermissionKeys.Declare(new (string Key, string[] Roles)[]
            {
                ("Notifications:View-Own", new[] { RoleNames.Registered }),
                ("notifications:send", Array.Empty<string>()),
                ("notifications:manage-channels", new[] { RoleNames.Admin, "Editors" }),
            });

            Assert.Equal(
                "View,Edit," +
                "notifications:view-own:" + RoleNames.Registered + ";Administrators," +
                "notifications:send:Administrators," +
                "notifications:manage-channels:Administrators;Editors",
                declaration);

            var parsed = PermissionKeys.ParseKeys(declaration);
            Assert.Equal(new[] { RoleNames.Registered, RoleNames.Admin }, parsed.Single(entry => entry.Operation == "view-own").Roles);
            Assert.Equal(new[] { RoleNames.Admin }, parsed.Single(entry => entry.Operation == "send").Roles);
        }

        [Fact]
        public void Declare_with_per_key_roles_equals_the_uniform_form_when_every_key_has_the_same_roles()
        {
            var keys = new[] { "pipelines:view", "Pipelines:Manage", "pipelines:view" };

            Assert.Equal(
                PermissionKeys.Declare(keys, "Editors"),
                PermissionKeys.Declare(keys.Select(key => (key, new[] { "Editors" }))));
        }

        [Fact]
        public void Declare_with_per_key_roles_rejects_what_the_uniform_form_rejects_and_a_key_declared_twice_with_different_roles()
        {
            Assert.Throws<ArgumentNullException>(() => PermissionKeys.Declare((IEnumerable<(string, string[])>)null!));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { ("user:write", Array.Empty<string>()) }));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { ("nocolon", Array.Empty<string>()) }));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { ("a:b:c", Array.Empty<string>()) }));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { (new string('g', 51) + ":manage", Array.Empty<string>()) }));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { ("pipelines:view", new[] { "Bad;Role" }) }));
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[]
            {
                ("pipelines:view", new[] { "Editors" }),
                ("Pipelines:View", new[] { RoleNames.Registered }),
            }));
        }

        [Fact]
        public void Split_takes_the_LAST_colon_so_a_dotted_resource_survives()
        {
            var (resource, operation) = PermissionKeys.Split("Platform.Modules:Manage");

            Assert.Equal("platform.modules", resource);
            Assert.Equal("manage", operation);
        }

        [Fact]
        public void IsKey_separates_a_lifted_key_from_a_framework_policy()
        {
            Assert.True(PermissionKeys.IsKey("gatetest:manage"));
            Assert.True(PermissionKeys.IsKey("platform.modules:manage"));
            Assert.False(PermissionKeys.IsKey("GateTest:Manage"));
            Assert.False(PermissionKeys.IsKey("user:write"));
            Assert.False(PermissionKeys.IsKey("gatetest"));
            Assert.False(PermissionKeys.IsKey("gatetest:manage:Administrators"));
        }

        // ---- [AllowAnonymous] on a key-gated endpoint (the bypass the mediator half survives) --------------

        [Fact]
        public void No_key_gated_endpoint_is_reachable_anonymously()
        {
            var endpoints = DiscoveredKeyGatedEndpoints();
            foreach (var endpoint in endpoints)
            {
                _output.WriteLine($"key-gated endpoint: {endpoint.Owner} [{endpoint.Key}] anonymous={endpoint.AllowsAnonymous}");
            }

            // Not vacuous: the BL-99 fixture controller's key-gated action is in the discovered set.
            Assert.Contains(endpoints, endpoint => endpoint.Owner.Contains(nameof(GateTestController), StringComparison.Ordinal));

            // The gate's own planted fixtures must NOT reach the real set, or it could never be green.
            Assert.DoesNotContain(endpoints, endpoint => endpoint.Owner.Contains("Planted", StringComparison.Ordinal));

            var violations = ValidateKeyGatedEndpoints(endpoints);
            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.Empty(violations);
        }

        [Fact]
        public void The_endpoint_gate_fails_a_planted_allow_anonymous_controller()
        {
            var classLevel = DiscoverKeyGatedEndpoints(new[] { typeof(PlantedAnonymousController) });
            var methodLevel = DiscoverKeyGatedEndpoints(new[] { typeof(PlantedMethodAnonymousController) });
            var clean = DiscoverKeyGatedEndpoints(new[] { typeof(PlantedGatedController) });

            // The control for the control: discovery found the gated action in all three.
            Assert.NotEmpty(classLevel);
            Assert.NotEmpty(methodLevel);
            Assert.NotEmpty(clean);

            foreach (var violation in ValidateKeyGatedEndpoints(classLevel).Concat(ValidateKeyGatedEndpoints(methodLevel)))
            {
                _output.WriteLine(violation);
            }

            Assert.NotEmpty(ValidateKeyGatedEndpoints(classLevel));
            Assert.NotEmpty(ValidateKeyGatedEndpoints(methodLevel));
            Assert.Empty(ValidateKeyGatedEndpoints(clean));
        }

        // ---- [Requires] on a METHOD of a mediator type (never enforced) -----------------------------------

        [Fact]
        public void No_requires_attribute_sits_on_a_method_of_a_mediator_type()
        {
            var attributes = DiscoveredRequirements();
            foreach (var requirement in attributes.Where(item => item.IsMethodLevelOnMediatorType))
            {
                _output.WriteLine($"method-level [Requires] on a mediator type: {requirement.Owner}");
            }

            Assert.Empty(ValidateRequirements(attributes, DiscoveredDefinitions().Select(definition => (definition.Name, definition.PermissionNames))));
        }

        [Fact]
        public void The_requires_gate_fails_a_method_level_attribute_on_a_mediator_type()
        {
            // AuthorizationBehavior collects the attributes of the REQUEST TYPE only - IsAllowedAsync(request
            // .GetType()) - so a [Requires] an author puts on the HANDLER METHOD is silently not enforced.
            var violations = ValidateRequirements(
                new[] { new Requirement("PlantedHandler.Handle", null, GateTestPermissions.Manage, false, true) },
                new[] { ("PlantedGood", PermissionKeys.Declare(GateTestPermissions.All, RoleNames.Admin)) });

            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.NotEmpty(violations);
        }

        // ---- [Requires] on a CONTROLLER type or action (never enforced, D105) ------------------------------

        [Fact]
        public void No_requires_attribute_sits_on_a_controller_type_or_action()
        {
            var attributes = DiscoveredRequirements();
            foreach (var requirement in attributes.Where(item => item.IsOnController))
            {
                _output.WriteLine($"[Requires] on a controller: {requirement.Owner}");
            }

            // Not vacuous: the scan does reach controllers (the BL-99 fixture controller is a loaded type), and
            // the planted controllers below are kept out of the real set.
            Assert.Contains(LoadedTypes(), type => type == typeof(GateTestController));
            Assert.DoesNotContain(attributes, item => item.Owner.Contains("Planted", StringComparison.Ordinal));

            var violations = ValidateRequirements(
                attributes.Where(item => item.IsOnController),
                DiscoveredDefinitions().Select(definition => (definition.Name, definition.PermissionNames)));
            foreach (var violation in violations)
            {
                _output.WriteLine(violation);
            }

            Assert.Empty(violations);
        }

        [Fact]
        public void The_requires_gate_fails_a_planted_controller_attribute()
        {
            // D105 refutation 1: no MVC filter reads RequiresAttribute - only the mediator's AuthorizationBehavior
            // does, and only on the REQUEST type. A [Requires] on a controller compiles, reads as a paywall, and
            // lets every caller through.
            var typeLevel = DiscoverRequirements(new[] { typeof(PlantedRequiresController) });
            var actionLevel = DiscoverRequirements(new[] { typeof(PlantedRequiresActionController) });
            var attributeOnly = DiscoverRequirements(new[] { typeof(PlantedApiControllerAttributeType) });
            var clean = DiscoverRequirements(new[] { typeof(PlantedFeatureGatedRequest) });

            // The control for the control: discovery found the attribute in every input.
            Assert.Single(typeLevel);
            Assert.Single(actionLevel);
            Assert.Single(attributeOnly);
            Assert.Single(clean);

            var definitions = new[] { ("PlantedGood", PermissionKeys.Declare(GateTestPermissions.All, RoleNames.Admin)) };
            foreach (var violation in ValidateRequirements(typeLevel.Concat(actionLevel).Concat(attributeOnly), definitions))
            {
                _output.WriteLine(violation);
            }

            Assert.NotEmpty(ValidateRequirements(typeLevel, definitions));
            Assert.NotEmpty(ValidateRequirements(actionLevel, definitions));
            Assert.NotEmpty(ValidateRequirements(attributeOnly, definitions));
            Assert.Empty(ValidateRequirements(clean, definitions));
        }

        // ---- the vocabulary halves must implement ONE rule -------------------------------------------------

        [Fact]
        public void TrySplit_and_Declare_agree_on_what_a_key_is()
        {
            // Declare rejects a 3-segment key; the LOOKUP must reject it too, or the same string means one
            // thing where the row is written and another where it is read.
            Assert.Throws<ArgumentException>(() => PermissionKeys.Declare(new[] { "a:b:c" }));
            Assert.False(PermissionKeys.IsKey("a:b:c"));
            Assert.False(PermissionKeys.TrySplit("a:b:c", out _, out _));

            // The control: a well-formed key (a DOTTED resource is still one segment) still splits.
            Assert.True(PermissionKeys.TrySplit("Platform.Modules:Manage", out var resource, out var operation));
            Assert.Equal("platform.modules", resource);
            Assert.Equal("manage", operation);
        }

        [Fact]
        public void ParseKeys_normalises_a_hand_written_declaration_so_the_row_and_the_lookup_agree()
        {
            // A module installed as a .nupkg can write PermissionNames by hand, and an UPPERCASE resource
            // diverges by provider: the row query is case-SENSITIVE on PostgreSQL/SQLite and case-INSENSITIVE
            // on RavenDB, while the guard always lower-cases the key. Normalise at the boundary (D49).
            var keys = PermissionKeys.ParseKeys("View,Edit,Pipelines:Manage:Administrators");
            var key = Assert.Single(keys);
            Assert.Equal("pipelines", key.Resource);
            Assert.Equal("manage", key.Operation);
            Assert.Equal("pipelines:manage", key.Key);

            // The grid column is built from Entry, and the row it writes must be the row the guard reads.
            Assert.Equal("pipelines:manage:Administrators", key.Entry);

            // A half over the column bound is DROPPED rather than written at a length PostgreSQL rejects and
            // SQLite accepts - the key then simply has no rows and is denied on every provider.
            Assert.Empty(PermissionKeys.ParseKeys("View,Edit," + new string('g', PermissionKeys.MaxSegmentLength + 1) + ":manage:Administrators"));

            // Framework API policies are NOT keys and keep their exact casing - they must match the constant.
            var entries = PermissionKeys.ParseEntries($"{PermissionNames.View},{PermissionNames.Edit},{EntityNames.User}:{PermissionNames.Write}:{RoleNames.Admin}");
            Assert.Equal(EntityNames.User, Assert.Single(entries).Resource);
            Assert.Empty(PermissionKeys.ParseKeys($"{EntityNames.User}:{PermissionNames.Write}:{RoleNames.Admin}"));
        }
        // ---- the validators (the gate itself) -------------------------------------------------------------

        internal sealed record Requirement(string Owner, string Feature, string Permission, bool IsNonSendContract, bool IsMethodLevelOnMediatorType = false, bool IsOnController = false);

        /// <summary>
        /// The declaration rules. Returns one line per violation; an empty list is a pass.
        /// </summary>
        internal static List<string> ValidateDeclarations(IEnumerable<(string Name, string PermissionNames)> definitions)
        {
            var violations = new List<string>();
            var resourceOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, permissionNames) in definitions)
            {
                var entries = PermissionKeys.Entries(permissionNames);
                if (entries.Count == 0)
                {
                    continue;
                }

                var declaresAKey = false;

                foreach (var entry in entries)
                {
                    var segments = entry.Split(PermissionKeys.Separator);

                    if (segments.Length == 1)
                    {
                        continue; // a plain UI permission name (View/Edit/Utilize/...)
                    }

                    if (segments.Length != 3)
                    {
                        violations.Add($"{name}: '{entry}' has {segments.Length} segments; a permission declaration is 'resource:operation:Roles' (a bare 2-segment key is silently ignored, F2).");
                        continue;
                    }

                    var resource = segments[0];
                    var operation = segments[1];
                    var roles = segments[2];

                    if (resource.Length == 0 || operation.Length == 0 || roles.Length == 0)
                    {
                        violations.Add($"{name}: '{entry}' has an empty segment.");
                        continue;
                    }

                    if (PermissionKeys.IsFrameworkEntity(resource))
                    {
                        // A framework API policy, not a lifted key - it must name the EntityNames constant exactly.
                        if (!PermissionKeys.FrameworkEntities.Contains(resource, StringComparer.Ordinal))
                        {
                            violations.Add($"{name}: '{entry}' names the framework entity '{resource}' with the wrong casing; use the EntityNames constant.");
                        }
                        continue;
                    }

                    declaresAKey = true;

                    if (!PermissionKeys.IsKey(resource + PermissionKeys.Separator + operation))
                    {
                        violations.Add($"{name}: '{entry}' is not a well-formed permission key - both halves must be lower-case, at most {PermissionKeys.MaxSegmentLength} characters, and the resource must not name an EntityNames constant.");
                        continue;
                    }

                    if (resourceOwners.TryGetValue(resource, out var owner) && owner != name)
                    {
                        violations.Add($"{name}: the resource '{resource}' is already declared by '{owner}'; a resource belongs to exactly one module.");
                    }
                    else
                    {
                        resourceOwners[resource] = name;
                    }
                }

                if (declaresAKey && !(entries.Count >= 2 && entries[0] == PermissionNames.View && entries[1] == PermissionNames.Edit))
                {
                    violations.Add($"{name}: a declaration carrying permission keys must start with '{PermissionNames.View},{PermissionNames.Edit}' (the control-panel add-module path assumes it); it starts with '{string.Join(",", entries.Take(2))}'.");
                }
            }

            return violations;
        }

        /// <summary>
        /// The <c>[Requires]</c> rules. Returns one line per violation; an empty list is a pass.
        /// </summary>
        internal static List<string> ValidateRequirements(
            IEnumerable<Requirement> requirements,
            IEnumerable<(string Name, string PermissionNames)> definitions)
        {
            // ParseKeys, so the set is the NORMALISED one the rows are written from - a [Requires] key is
            // lower-cased by TrySplit, and a declaration the parser normalised must still match it.
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, permissionNames) in definitions)
            {
                foreach (var entry in PermissionKeys.ParseKeys(permissionNames))
                {
                    declared.Add(entry.Key);
                }
            }

            var violations = new List<string>();
            foreach (var requirement in requirements)
            {
                var hasFeature = !string.IsNullOrWhiteSpace(requirement.Feature);
                var hasPermission = !string.IsNullOrWhiteSpace(requirement.Permission);

                if (!hasFeature && !hasPermission)
                {
                    violations.Add($"{requirement.Owner}: an empty [Requires()] gates nothing - give it a Feature, a Permission, or remove it.");
                    continue;
                }

                if (requirement.IsNonSendContract)
                {
                    violations.Add($"{requirement.Owner}: [Requires] on an IStreamRequest/INotification is NEVER enforced - the mediator pipeline exists only on Send (F15).");
                }

                if (requirement.IsMethodLevelOnMediatorType)
                {
                    // AuthorizationBehavior calls IsAllowedAsync(request.GetType()), so it collects the
                    // attributes of the REQUEST TYPE and nothing else. A [Requires] on a handler method (or on
                    // any other member of a mediator type) compiles, reads as a gate, and gates nothing.
                    violations.Add($"{requirement.Owner}: [Requires] on a METHOD of a mediator request/handler is NEVER enforced - the mediator gate is TYPE-level only (AuthorizationBehavior sees request.GetType()). Move it to the request type.");
                }

                if (requirement.IsOnController)
                {
                    // D105 refutation 1: RequiresAttribute is read by the mediator's AuthorizationBehavior and by
                    // nothing else. On a controller it is decoration - every caller reaches the action.
                    violations.Add($"{requirement.Owner}: [Requires] on a controller type or action is NEVER enforced - no MVC filter reads it; dispatch a mediator request that carries [Requires] (D105)");
                }

                if (!hasPermission)
                {
                    continue;
                }

                if (string.Equals(requirement.Permission, "system:admin", StringComparison.Ordinal))
                {
                    continue; // the one key that maps to Host role membership rather than to rows (D99)
                }

                if (!PermissionKeys.TrySplit(requirement.Permission, out var resource, out var operation))
                {
                    violations.Add($"{requirement.Owner}: '{requirement.Permission}' is not a 'resource:operation' permission key.");
                    continue;
                }

                if (!declared.Contains(resource + PermissionKeys.Separator + operation))
                {
                    violations.Add($"{requirement.Owner}: '{requirement.Permission}' is declared by no module definition's PermissionNames - a key nothing declares seeds no row and therefore DENIES everyone.");
                }
            }

            return violations;
        }

        /// <summary>One controller action (or controller) gated by <c>[RequiresPermissionKey]</c>.</summary>
        internal sealed record KeyGatedEndpoint(string Owner, string Key, bool AllowsAnonymous, string AnonymousSource);

        /// <summary>
        /// The endpoint rules. Returns one line per violation; an empty list is a pass.
        /// </summary>
        /// <remarks>
        /// ONE rule, and it is the asymmetry that makes the mistake dangerous. <c>[RequiresPermissionKey]</c>
        /// is an ordinary <c>AuthorizeAttribute</c>, and ASP.NET Core's authorization middleware SHORT-CIRCUITS
        /// on <c>[AllowAnonymous]</c> endpoint metadata however many policies the endpoint carries - while the
        /// mediator's <c>[Requires]</c> on the command behind it still denies. Driven on this branch's host:
        /// an <c>[AllowAnonymous]</c> controller's key-gated action returned 200 to an anonymous caller and its
        /// ungated dispatching action returned 403, so an author who tests the dispatching one concludes the
        /// controller is gated. D99 booked this as a per-module DoD sentence; prose is not a gate, and an
        /// external module escapes prose entirely.
        /// </remarks>
        internal static List<string> ValidateKeyGatedEndpoints(IEnumerable<KeyGatedEndpoint> endpoints)
        {
            var violations = new List<string>();
            foreach (var endpoint in endpoints)
            {
                if (endpoint.AllowsAnonymous)
                {
                    violations.Add($"{endpoint.Owner}: [RequiresPermissionKey(\"{endpoint.Key}\")] is DEAD - {endpoint.AnonymousSource} carries [AllowAnonymous], and the authorization middleware short-circuits on that metadata no matter how many policies the endpoint declares.");
                }
            }

            return violations;
        }

        /// <summary>
        /// Every <c>[RequiresPermissionKey]</c> in the given types, with whether an <c>[AllowAnonymous]</c>
        /// on the class or the action would short-circuit it.
        /// </summary>
        internal static List<KeyGatedEndpoint> DiscoverKeyGatedEndpoints(IEnumerable<Type> types)
        {
            var endpoints = new List<KeyGatedEndpoint>();
            foreach (var type in types)
            {
                var classAnonymous = type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

                foreach (var attribute in type.GetCustomAttributes<RequiresPermissionKeyAttribute>(inherit: true))
                {
                    endpoints.Add(new KeyGatedEndpoint(type.FullName, attribute.Key, classAnonymous, "the class"));
                }

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var methodAnonymous = method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();
                    foreach (var attribute in method.GetCustomAttributes<RequiresPermissionKeyAttribute>(inherit: true))
                    {
                        endpoints.Add(new KeyGatedEndpoint(
                            $"{type.FullName}.{method.Name}",
                            attribute.Key,
                            classAnonymous || methodAnonymous,
                            classAnonymous ? "its controller" : "the action"));
                    }
                }
            }

            return endpoints;
        }

        // ---- the gate's own planted fixtures (known-dirty and known-clean input) --------------------------
        //
        // Private NESTED types: MVC's controller feature provider only ever considers public, non-nested
        // types, so none of these is ever registered as a real endpoint. The real scan excludes them by
        // declaring type - they exist to prove the validator fails on dirty input and passes on clean.

        [AllowAnonymous]
        private sealed class PlantedAnonymousController
        {
            [RequiresPermissionKey(GateTestPermissions.Manage)]
            public string Secret() => "";
        }

        private sealed class PlantedMethodAnonymousController
        {
            [AllowAnonymous]
            [RequiresPermissionKey(GateTestPermissions.Manage)]
            public string Secret() => "";
        }

        private sealed class PlantedGatedController
        {
            [RequiresPermissionKey(GateTestPermissions.Manage)]
            public string Secret() => "";
        }

        [Requires(Feature = "planted.pro")]
        private sealed class PlantedRequiresController : Microsoft.AspNetCore.Mvc.Controller
        {
            public string Run() => "";
        }

        private sealed class PlantedRequiresActionController : Microsoft.AspNetCore.Mvc.ControllerBase
        {
            [Requires(Feature = "planted.pro")]
            public string Run() => "";
        }

        // A POCO controller by attribute alone - MVC's other convention.
        [Microsoft.AspNetCore.Mvc.ApiController]
        [Requires(Feature = "planted.pro")]
        private sealed class PlantedApiControllerAttributeType
        {
        }

        // Known-clean: the attribute where it IS enforced - on a mediator request type.
        [Requires(Feature = "planted.pro")]
        private sealed class PlantedFeatureGatedRequest : IRequest<string>
        {
        }
        // ---- discovery --------------------------------------------------------------------------------------

        private static List<ModuleDefinition> DiscoveredDefinitions()
        {
            ForceLoadFrameworkModuleAssemblies();
            return ModuleDefinitionRepository.GetModuleDefinitionsFromAssemblies();
        }

        private static List<KeyGatedEndpoint> DiscoveredKeyGatedEndpoints()
        {
            return DiscoverKeyGatedEndpoints(LoadedTypes().Where(type => type.DeclaringType != typeof(PermissionKeyDeclarationGateTests)));
        }

        private static List<Type> LoadedTypes()
        {
            ForceLoadFrameworkModuleAssemblies();

            var types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetStommeAssemblies())
            {
                try
                {
                    types.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types.AddRange(ex.Types.Where(type => type != null));
                }
            }

            return types;
        }

        private static List<Requirement> DiscoveredRequirements()
        {
            // The gate's own planted fixtures are excluded by declaring type, exactly as the endpoint scan does -
            // otherwise the known-dirty input would make the real-tree gate red forever.
            return DiscoverRequirements(LoadedTypes().Where(type => type.DeclaringType != typeof(PermissionKeyDeclarationGateTests)));
        }

        /// <summary>Every <c>[Requires]</c> on the given types and their declared methods.</summary>
        internal static List<Requirement> DiscoverRequirements(IEnumerable<Type> types)
        {
            var requirements = new List<Requirement>();
            foreach (var type in types)
            {
                var isNonSendContract = type.GetInterfaces().Any(contract =>
                    contract == typeof(INotification) ||
                    (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IStreamRequest<>)));
                var isController = IsControllerType(type);

                foreach (var attribute in type.GetCustomAttributes<RequiresAttribute>(inherit: false))
                {
                    requirements.Add(new Requirement(type.FullName, attribute.Feature, attribute.Permission, isNonSendContract, IsOnController: isController));
                }

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    foreach (var attribute in method.GetCustomAttributes<RequiresAttribute>(inherit: false))
                    {
                        requirements.Add(new Requirement($"{type.FullName}.{method.Name}", attribute.Feature, attribute.Permission, false, IsMediatorType(type), isController));
                    }
                }
            }

            return requirements;
        }

        /// <summary>
        /// True when MVC would treat the type as a controller by either of its conventions: it derives from
        /// <see cref="Microsoft.AspNetCore.Mvc.ControllerBase"/>, or it carries <c>[Controller]</c> /
        /// <c>[ApiController]</c> (inherited attributes included). Nesting and visibility are deliberately NOT
        /// considered: the rule is about where an author PUT the attribute, not whether MVC registered the type.
        /// </summary>
        private static bool IsControllerType(Type type)
        {
            return typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type)
                || type.GetCustomAttributes<Microsoft.AspNetCore.Mvc.ControllerAttribute>(inherit: true).Any()
                || type.GetCustomAttributes<Microsoft.AspNetCore.Mvc.ApiControllerAttribute>(inherit: true).Any();
        }

        /// <summary>
        /// True when the type is a mediator request, notification or handler - i.e. a type whose members are
        /// reached ONLY through <c>IMediator</c>, where the behavior pipeline sees the REQUEST TYPE alone.
        /// </summary>
        private static bool IsMediatorType(Type type)
        {
            return type.GetInterfaces().Any(contract =>
                contract == typeof(INotification) ||
                (contract.IsGenericType && (
                    contract.GetGenericTypeDefinition() == typeof(IRequest<>) ||
                    contract.GetGenericTypeDefinition() == typeof(IStreamRequest<>) ||
                    contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>) ||
                    contract.GetGenericTypeDefinition() == typeof(INotificationHandler<>) ||
                    contract.GetGenericTypeDefinition() == typeof(IStreamRequestHandler<,>))));
        }

        // GetStommeAssemblies only sees LOADED assemblies, and the framework's own modules live in
        // Stomme.Client - which a test that has touched no client type has never loaded. Touching one type
        // forces the load, so the gate cannot pass by simply not looking at the stock modules.
        private static void ForceLoadFrameworkModuleAssemblies()
        {
            _ = typeof(Stomme.Modules.Admin.Users.ModuleInfo).Assembly;
            _ = typeof(GateTestPermissions).Assembly;
        }
    }
}