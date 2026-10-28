using Stomme.Models;
using Stomme.Modules;

namespace Stomme.Modules.Guestbook
{
    /// <summary>
    /// The Guestbook module's <see cref="ModuleDefinition"/> — how the framework discovers it. The
    /// <c>ModuleDefinitionName</c> is derived from this type's namespace + assembly
    /// (<c>Stomme.Modules.Guestbook, Stomme.Client</c>), which is also how the Blazor host resolves the
    /// rendered component: <c>{namespace}.{action}</c> → <c>Stomme.Modules.Guestbook.Index</c>.
    /// <para>
    /// <c>ServerManagerType</c> is intentionally left empty: this module has no relational migration to run
    /// (its store is a standalone SQLite database created by <c>GuestbookSeeder</c> via EnsureCreated), so
    /// there is no <c>IInstallable</c> step for the install pipeline to invoke.
    /// </para>
    /// </summary>
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "Guestbook",
            Description = "A CQRS + DDD sample module: sign a public guestbook. Showcases the Stomme.Mediator " +
                          "pipeline (command/query/notification/stream + validation/logging/performance/exception " +
                          "behaviors) over a DDD aggregate on the clean-arch persistence stack.",
            Categories = "Sample",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
        };
    }
}
