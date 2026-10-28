#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Portability;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;

namespace Stomme.Modules.Portability.Contributions
{
    /// <summary>
    /// A placed module's content, through the module's own <c>IPortable</c> (BL-82, D107 item 2) - the adapter that
    /// lets every module that already supports Export/Import Module travel in a package without a line of new code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where it goes.</b> A package cannot create pages or place modules, so an import lands on the module that ALREADY
    /// sits in the same place on the target site: the page with the same path, the same pane, the same module
    /// definition, the same title. No such module is a <c>Skipped</c> line saying so. Rename has nothing to rename - a
    /// module's place is its identity - so it skips too.
    /// </para>
    /// <para>
    /// <b>What is checked before anything changes</b> (refutation 10): the target is on the CURRENT site (the lookup is
    /// site-scoped - an id from the package is never trusted); the caller holds Edit on the target PAGE - a portability
    /// key never replaces per-page Edit; and the package sets no setting the target keeps PRIVATE - Import Module
    /// overwrites every setting it names, private ones included, so the adapter refuses rather than pass one through.
    /// Private settings are never exported either (<c>ExportModuleAsync</c> leaves them out). The markup a module stores
    /// is trusted exactly as Import Module trusts it - no more, no less.
    /// </para>
    /// </remarks>
    public sealed class ModuleContentContributor(
        IModuleRepository modules,
        IModuleDefinitionRepository definitions,
        IPageModuleRepository pageModules,
        IPageRepository pages,
        ISettingRepository settings,
        IUserPermissions permissions,
        IHttpContextAccessor accessor) : IPortableEntityContributor
    {
        public const string Type = "module-content";

        public string EntityType => Type;

        public int CurrentSchemaVersion => 1;

        public IReadOnlyCollection<int> ReadableSchemaVersions => new[] { 1 };

        public async Task<IReadOnlyList<PortableEntity>> ExportAsync(PortabilityScope scope, CancellationToken cancellationToken = default)
        {
            var placed = (await pageModules.GetPageModulesAsync(scope.SiteId)).Where(pm => !pm.IsDeleted && pm.Module is { IsDeleted: false }).ToList();
            var byPage = (await pages.GetPagesAsync(scope.SiteId)).ToDictionary(page => page.PageId);
            var wanted = scope.EntityIds?.ToHashSet(StringComparer.Ordinal);
            var portable = (await definitions.GetModuleDefinitionsAsync(scope.SiteId))
                .Where(IsPortable).Select(definition => definition.ModuleDefinitionName).ToHashSet(StringComparer.Ordinal);

            var entities = new List<PortableEntity>();
            foreach (var group in placed.GroupBy(pm => pm.ModuleId).OrderBy(g => g.Key))
            {
                var id = group.Key.ToString(CultureInfo.InvariantCulture);
                if (wanted is not null && !wanted.Contains(id))
                {
                    continue;
                }

                var first = group.OrderBy(pm => pm.PageModuleId).First();
                if (!byPage.TryGetValue(first.PageId, out var page) || page.UserId is not null)
                {
                    continue; // a personal page's module is that person's, not the site's configuration
                }

                var module = await modules.GetModuleAsync(first.ModuleId);
                if (module is null || module.SiteId != scope.SiteId || !portable.Contains(module.ModuleDefinitionName))
                {
                    if (wanted is not null)
                    {
                        throw new InvalidOperationException($"Module {id} has no portable content.");
                    }

                    continue; // only a module whose own IPortable exports content travels - never an admin module's settings
                }

                var content = await modules.ExportModuleAsync(module.ModuleId);
                if (string.IsNullOrEmpty(content))
                {
                    if (wanted is not null)
                    {
                        throw new InvalidOperationException($"Module {id} could not be exported.");
                    }

                    continue; // not every module has content to move; a whole-type export carries the ones that do
                }

                entities.Add(new PortableEntity(
                    new PortableEntityRef(Type, id),
                    $"{page.Path}/{first.Pane}/{first.Title}",
                    1,
                    JsonSerializer.Serialize(new Payload(module.ModuleDefinitionName, page.Path ?? string.Empty, first.Pane ?? string.Empty, first.Title ?? string.Empty, content))));
            }

            return entities;
        }

        public async Task<string?> ValidateImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default)
        {
            var payload = Read(entity);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ModuleDefinitionName) || string.IsNullOrWhiteSpace(payload.Content))
            {
                return "the module content is incomplete";
            }

            var target = await FindTargetAsync(context.SiteId, payload);
            if (target is null)
            {
                return null; // nothing to change: imported as Skipped, with the reason
            }

            var principal = accessor.HttpContext?.User;
            if (principal is null || !await permissions.IsAuthorizedAsync(principal, context.SiteId, EntityNames.Page, target.Value.Page.PageId, PermissionNames.Edit))
            {
                return $"you may not edit the page '{payload.PagePath}'";
            }

            var incoming = SettingNames(payload.Content);
            var privateNames = (await settings.GetSettingsAsync(EntityNames.Module, target.Value.Module.ModuleId))
                .Where(setting => setting.IsPrivate).Select(setting => setting.SettingName).ToHashSet(StringComparer.Ordinal);
            var clash = incoming.Where(privateNames.Contains).ToList();
            if (clash.Count > 0)
            {
                return $"the package would overwrite private settings of the module on '{payload.PagePath}': {string.Join(", ", clash)}";
            }

            return null;
        }

        public async Task<EntityImportOutcome> ImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default)
        {
            var payload = Read(entity)!;
            var target = await FindTargetAsync(context.SiteId, payload);
            if (target is null)
            {
                return new EntityImportOutcome(EntityImportResult.Skipped, null, $"no {payload.ModuleDefinitionName} titled '{payload.Title}' in '{payload.Pane}' on '{payload.PagePath}' here");
            }

            var targetId = target.Value.Module.ModuleId.ToString(CultureInfo.InvariantCulture);
            if (context.Strategy != ConflictStrategy.Overwrite)
            {
                return new EntityImportOutcome(EntityImportResult.Skipped, targetId, "the module is there; only Overwrite replaces its content");
            }

            return await modules.ImportModuleAsync(target.Value.Module.ModuleId, payload.Content)
                ? new EntityImportOutcome(EntityImportResult.Overwritten, targetId)
                : new EntityImportOutcome(EntityImportResult.Failed, targetId, "the module did not accept the content");
        }

        private async Task<(Page Page, Module Module)?> FindTargetAsync(int siteId, Payload payload)
        {
            var page = await pages.GetPageAsync(payload.PagePath, siteId);
            if (page is null || page.SiteId != siteId)
            {
                return null;
            }

            var match = (await pageModules.GetPageModulesAsync(siteId)).FirstOrDefault(pm =>
                pm.PageId == page.PageId && !pm.IsDeleted && pm.Module is { IsDeleted: false } module
                && module.SiteId == siteId
                && string.Equals(module.ModuleDefinitionName, payload.ModuleDefinitionName, StringComparison.Ordinal)
                && string.Equals(pm.Pane, payload.Pane, StringComparison.Ordinal)
                && string.Equals(pm.Title, payload.Title, StringComparison.Ordinal));
            return match is null ? null : (page, match.Module);
        }

        private static bool IsPortable(ModuleDefinition definition) =>
            !string.IsNullOrEmpty(definition.ServerManagerType)
            && System.Type.GetType(definition.ServerManagerType)?.GetInterface(nameof(Stomme.Modules.IPortable)) is not null;

        private static IReadOnlyCollection<string> SettingNames(string content)
        {
            try
            {
                var modulecontent = JsonSerializer.Deserialize<ModuleContent>(content.Replace("\n", ""));
                return modulecontent?.Settings?.Keys.ToList() ?? new List<string>();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        private static Payload? Read(PortableEntity entity)
        {
            try
            {
                return JsonSerializer.Deserialize<Payload>(entity.PayloadJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>What travels: where the module sits, and what its own <c>IPortable</c> exported.</summary>
        public sealed record Payload(string ModuleDefinitionName, string PagePath, string Pane, string Title, string Content);
    }
}
