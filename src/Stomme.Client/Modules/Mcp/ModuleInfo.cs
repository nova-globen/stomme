using System.Collections.Generic;
using Stomme.Mcp;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Mcp
{
    /// <summary>
    /// The MCP module's <see cref="ModuleDefinition"/> (BL-80 / D101 item 7) - how the framework discovers it, what it
    /// declares to the authorization bridge, and where it lands on a fresh site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This file is what makes every MCP gate real.</b> A permission key that NO module definition declares has no
    /// seeded rows, so anything gated on it denies everyone - correct, and useless. That is why the built-in
    /// <c>stomme://site</c> contributor could not be built before this: its gate is <see cref="McpPermissions.View"/>,
    /// and without this declaration it would have published a resource nobody on any site could read.
    /// </para>
    /// <para>
    /// <b>It declares NO page template, on purpose.</b> A module-declared page seeds on every install that has the
    /// assembly, and the only knob over it is the framework's <c>module.&lt;id&gt;.enabled</c> feature flag - a SECOND
    /// switch beside <c>Mcp:Enabled</c>, which an operator turning MCP on would then also have to find. D101 asks for
    /// the page to follow <c>Mcp:Enabled</c>, so it is declared in <c>AdminSiteTemplate</c> (server-side, where the
    /// configuration is) beside the Data Export and Licensing pages, which are opt-in the same way. A stock install
    /// gains no page, and switching MCP on later reaches an EXISTING site through the additive boot-path
    /// reconciliation (<c>SiteRepository.ProcessAdminPageTemplatesAsync</c>).
    /// </para>
    /// <para>
    /// <b>One page, three controls.</b> The page places the module and renders <c>Index</c> (the external servers);
    /// <c>Catalog</c> and <c>Audit</c> are module ACTIONS reached from it. Each control gates ITSELF on its own key
    /// through <c>IsAuthorizedPermission</c>, and the server decides again per request - so a control a user should not
    /// see is not merely hidden.
    /// </para>
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "MCP";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Model Context Protocol: the external MCP servers this site has registered, what this install " +
                          "publishes to MCP clients, and the audit ledger of every call.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(McpPermissions.Declarations)
        };
    }
}
