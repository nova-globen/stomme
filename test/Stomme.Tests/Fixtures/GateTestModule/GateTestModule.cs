using System.Collections.Generic;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;

namespace Stomme.Tests.Fixtures.GateTestModule
{
    /// <summary>
    /// The permission-key vocabulary of the gate fixture - the shape a module's
    /// <c>XxxPermissions</c> class takes (a flat static class of <c>resource:operation</c> constants),
    /// without its <c>IPermissionContributor</c> registration.
    /// </summary>
    public static class GateTestPermissions
    {
        public const string Manage = "gatetest:manage";
        public const string View = "gatetest:view";

        public static readonly string[] All = { Manage, View };
    }

    /// <summary>
    /// A test-only module that declares permission KEYS, used by the permission gates. Discovered by the very same
    /// assembly scan every other suite already runs over this test assembly (<c>IsStommeAssembly</c> matches
    /// on the name "Stomme.Tests"), exactly like the other fixtures beside it - and it cannot leak
    /// into a production scan, because the only assembly that carries it is the test assembly.
    /// </summary>
    /// <remarks>
    /// It is INERT everywhere else: no <c>[Route]</c> on its control, so it contributes no PageTemplate and
    /// seeds no page; its keys live under the <c>gatetest</c> resource, which nothing else declares.
    /// <para>
    /// <see cref="DeclaredKeys"/> is settable so ONE process can boot two hosts over one database with a
    /// DIFFERENT declaration each time - the upgrade shape the reconciliation gate drives (the
    /// <c>TransportDeclaringTestModule</c> precedent). Every value it is ever set to must still be a valid
    /// declaration, because the declaration gate may run concurrently in the same process.
    /// </para>
    /// </remarks>
    public sealed class ModuleInfo : IModule
    {
        public static IReadOnlyList<string> DeclaredKeys { get; set; } = GateTestPermissions.All;

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "GateTest",
            Description = "Fixture: a module that declares permission keys",
            PermissionNames = PermissionKeys.Declare(DeclaredKeys, RoleNames.Admin)
        };
    }

    /// <summary>The module's one UI control - present so the definition is a normal (non-headless) module.</summary>
    public sealed class Index : IModuleControl
    {
        public SecurityAccessLevel SecurityAccessLevel => SecurityAccessLevel.View;
        public string Title => "Gate Test";
        public string Actions => "";
        public bool UseAdminContainer => true;
        public List<Resource> Resources => null;
        public string RenderMode => "";
        public bool? Prerender => null;
    }

    /// <summary>A gated command: the shape every <c>[Requires]</c> declaration takes.</summary>
    [Requires(GateTestPermissions.Manage)]
    public sealed class GateTestManageCommand : IRequest<string>
    {
    }
}