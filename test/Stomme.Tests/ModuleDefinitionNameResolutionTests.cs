using System.Collections.Generic;
using System.Linq;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Repository;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    // BL-74: ModuleDefinitionRepository's control scan let an Index control's Title OVERWRITE the Name a
    // module declared through IModule — and ModuleFeatureFlags derives the module.<id>.enabled config knob
    // from the resulting name, so the flag an author would reasonably write (keyed on the declared name) did
    // nothing while a key derived from a UI title worked. Measured live on 4 fresh installs during the
    // idiomatic endgame proof-of-life: with Name = "Reference" and Index.Title = "Memos",
    // module.reference.enabled=false had NO effect and module.memos.enabled=false suppressed seeding.
    //
    // The resolution contract these tests pin: a name DECLARED via IModule is authoritative; the Index
    // control's Title names only a convention-only module (one with no IModule), where it is the sole
    // human-readable name source. Both directions are pinned so the fix can be neither absent nor too wide.
    public class ModuleDefinitionNameResolutionTests
    {
        private static ModuleDefinition Definition(string moduleDefinitionName)
        {
            var definitions = ModuleDefinitionRepository.GetModuleDefinitionsFromAssemblies();
            var definition = definitions.SingleOrDefault(item => item.ModuleDefinitionName == moduleDefinitionName);
            Assert.NotNull(definition);
            return definition!;
        }

        [Fact]
        public void A_declared_module_name_is_authoritative_over_its_index_controls_title()
        {
            var definition = Definition("Stomme.Tests.Bl74DeclaredFixture, Stomme.Tests");

            Assert.Equal("Bl74DeclaredName", definition.Name);
        }

        [Fact]
        public void The_feature_key_follows_the_declared_name_not_the_title()
        {
            var definition = Definition("Stomme.Tests.Bl74DeclaredFixture, Stomme.Tests");

            // This is the D85 knob the author writes in features.json — it must answer to the name the
            // author declared, not to a UI title nobody wrote down.
            Assert.Equal("module.bl74declaredname.enabled", ModuleFeatureFlags.FeatureKey(definition.Name));
        }

        [Fact]
        public void A_convention_only_module_is_still_named_by_its_index_controls_title()
        {
            // The positive control for the other direction: a module with NO IModule has no declared name,
            // so the Index Title remains its name source. A fix that removes the Title mechanism entirely
            // (rather than subordinating it to a declared name) fails here.
            var definition = Definition("Stomme.Tests.Bl74TitleOnlyFixture, Stomme.Tests");

            Assert.Equal("Bl74 Title Only", definition.Name);
        }
    }
}

namespace Stomme.Tests.Bl74DeclaredFixture
{
    // A module whose declared Name and Index Title deliberately DISAGREE — the exact shape the endgame's
    // lifted modules take (ModuleInfo.Name = "Reference", Index.Title = "Memos"). Inert everywhere else:
    // no PageTemplates, so nothing seeds; discovered by the same assembly scan every suite already runs
    // over this test assembly (the TransportDeclaringTestModule precedent).
    public sealed class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "Bl74DeclaredName",
            Description = "BL-74 fixture: declared Name and Index Title disagree",
        };
    }

    public sealed class Index : IModuleControl
    {
        public SecurityAccessLevel SecurityAccessLevel => SecurityAccessLevel.View;
        public string Title => "Bl74 Control Title";
        public string Actions => "";
        public bool UseAdminContainer => true;
        public List<Resource> Resources => null;
        public string RenderMode => "";
        public bool? Prerender => null;
    }
}

namespace Stomme.Tests.Bl74TitleOnlyFixture
{
    // The convention-only shape: no IModule, so the Index Title is the only name source and must keep
    // working (this is how the stock Admin modules — OpenIddict "Identity Provider", DeadLetters
    // "Message Queue", Features "Feature Flags" — get their display names today).
    public sealed class Index : IModuleControl
    {
        public SecurityAccessLevel SecurityAccessLevel => SecurityAccessLevel.View;
        public string Title => "Bl74 Title Only";
        public string Actions => "";
        public bool UseAdminContainer => true;
        public List<Resource> Resources => null;
        public string RenderMode => "";
        public bool? Prerender => null;
    }
}
