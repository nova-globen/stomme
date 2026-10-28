using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Repo-wide gates over the way a module DECLARES its CMS placement — the "routable modules" mechanism:
    /// <see cref="ModuleDefinition.PageTemplates"/> read on every boot by
    /// <c>SiteRepository.ProcessPageTemplatesAsync</c>. Ported from the idiomatic endgame proof-of-life spike
    /// (BL-74), where every rule below was MEASURED failing on a live install
    /// (<c>docs/reports/endgame-proof-of-life-idiomatic.md</c>) — each fails SILENTLY: no exception, no log
    /// line, just a page that is not there or a child page created at the root. Exactly the class the endgame
    /// cannot afford to rediscover 36 times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spike's third gate — an Index control Title that disagrees with the declared module Name re-keys
    /// the <c>module.&lt;id&gt;.enabled</c> knob — is deliberately NOT ported as a convention ban: BL-74 fixed
    /// the mechanism instead (a declared Name is now authoritative; see
    /// <see cref="ModuleDefinitionNameResolutionTests"/>), so the disagreement is legal and harmless.
    /// </para>
    /// <para>
    /// The gates are reflective over the module assemblies this test project can see, so a NEW module is
    /// covered with no test edit. Pure reflection — no database, no host.
    /// </para>
    /// </remarks>
    public class ModulePlacementConventionTests
    {
        /// <summary>
        /// Anchored on concrete types so the assemblies are guaranteed loaded (a bare
        /// <c>AppDomain.GetAssemblies()</c> sweep is lazy and would silently scan nothing — a vacuous green).
        /// </summary>
        private static IReadOnlyList<Assembly> ModuleAssemblies() =>
        [
            typeof(Stomme.Modules.Guestbook.ModuleInfo).Assembly,   // Stomme.Client — Guestbook, HtmlText, admin
            typeof(ModulePlacementConventionTests).Assembly           // this project's fixture modules
        ];

        private static List<(Type Type, ModuleDefinition Definition)> DiscoverModules()
        {
            var found = new List<(Type, ModuleDefinition)>();
            foreach (var assembly in ModuleAssemblies().Distinct())
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type is not { IsClass: true, IsAbstract: false } ||
                        !typeof(IModule).IsAssignableFrom(type) ||
                        type.GetConstructor(Type.EmptyTypes) is null)
                    {
                        continue;
                    }

                    if (Activator.CreateInstance(type) is IModule module)
                    {
                        found.Add((type, module.ModuleDefinition));
                    }
                }
            }
            return found;
        }

        [Fact]
        public void The_scanner_finds_modules_at_all()
        {
            // Vacuity guard: a FLOOR, not an exact count — an exact count is a hand-maintained list in disguise.
            var modules = DiscoverModules();
            Assert.True(modules.Count >= 3, $"expected at least 3 IModule implementations, found {modules.Count}");
            Assert.Contains(modules, item => item.Definition.Name == "Guestbook");
        }

        /// <summary>
        /// <c>SiteRepository.CreatePagesAsync</c> processes a template only when
        /// <c>AliasName == "*" || alias == null || AliasName == alias.Name</c>. The boot/upgrade path passes a
        /// REAL alias, and <see cref="PageTemplate"/> defaults <c>AliasName</c> to the empty string — which
        /// matches nothing. A module page template that forgets this is SILENTLY skipped for ever.
        /// </summary>
        [Fact]
        public void Every_module_page_template_declares_an_alias_name()
        {
            var offenders = new List<string>();
            foreach (var (type, definition) in DiscoverModules())
            {
                foreach (var template in definition.PageTemplates ?? [])
                {
                    if (string.IsNullOrWhiteSpace(template.AliasName))
                    {
                        offenders.Add($"{type.FullName} -> page template '{template.Name}{template.Path}'");
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "A module page template with an empty AliasName is silently skipped on the boot path. " +
                "Set AliasName = \"*\" (all sites) or a specific alias name. Offenders: " +
                string.Join("; ", offenders));
        }

        /// <summary>
        /// <c>CreatePagesAsync</c> reads the site's pages ONCE and appends each newly created page to that
        /// in-memory list, resolving <c>Parent</c> against it. A child declared BEFORE its parent therefore
        /// finds no parent and is created at the ROOT — a wrong page tree, silently, with no error.
        /// </summary>
        [Fact]
        public void A_child_page_template_is_declared_after_its_parent()
        {
            var offenders = new List<string>();
            foreach (var (type, definition) in DiscoverModules())
            {
                var templates = (definition.PageTemplates ?? []).ToList();
                var seen = new List<string>();
                foreach (var template in templates)
                {
                    var parent = template.Parent;
                    if (!string.IsNullOrWhiteSpace(parent) &&
                        // only a parent this same module declares needs the ordering; a parent that already
                        // exists on the site (e.g. "admin") is resolved from the DB, not from this list.
                        templates.Any(item => PathOf(item).Equals(parent, StringComparison.OrdinalIgnoreCase)) &&
                        !seen.Any(item => item.Equals(parent, StringComparison.OrdinalIgnoreCase)))
                    {
                        offenders.Add($"{type.FullName} -> '{template.Name}' declares Parent '{parent}' before it is declared");
                    }
                    seen.Add(PathOf(template));
                }
            }

            Assert.True(offenders.Count == 0,
                "A child page template declared before its parent is created at the ROOT instead of under the " +
                "parent. Offenders: " + string.Join("; ", offenders));

            static string PathOf(PageTemplate template) =>
                string.IsNullOrEmpty(template.Path)
                    ? (string.IsNullOrEmpty(template.Parent) ? template.Name : template.Parent + "/" + template.Name)
                    : template.Path;
        }
    }
}
