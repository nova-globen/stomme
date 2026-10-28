#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;

namespace Stomme.Infrastructure.DemoContent
{
    /// <summary>
    /// The ONE switch for every module's demo content (BL-94 increment 14, D110 Q7): <c>DemoContent:Enabled</c>, DEFAULT OFF.
    /// the predecessor platform seeded its demo connections and datasets on every boot of every install; here a stock install carries
    /// none, and an evaluator turns the switch on in the override file. Read through <see cref="HostConfiguration"/>,
    /// never <c>IConfiguration</c> (BL-162).
    /// </summary>
    public static class DemoContentSettings
    {
        public const string Key = "DemoContent:Enabled";

        public static bool Enabled(IConfiguration? configuration) => configuration?.GetValue<bool>(Key) ?? false;
    }

    /// <summary>
    /// The per-tenant, per-site, per-module stamp that makes demo content ADDITIVE: a site stamped at a module's current
    /// demo version is left exactly as it is on every later boot - an operator's rename, edit or delete of a demo row
    /// survives - and a site not yet stamped receives what it lacks (each row matched by its own identity, so a
    /// half-applied set is completed, never duplicated) and is then stamped. Stored as a private site setting
    /// <c>DemoContent:&lt;module&gt;:&lt;tenant&gt;</c>. <b>The tenant is part of the name because two tenants over ONE database
    /// share the site-settings table and a site id</b> (the site-per-alias topology): a stamp keyed on the site alone
    /// made the second tenant look already applied and left it empty - measured on the first run of the two-tenant case.
    /// </summary>
    public sealed class DemoContentStamp(ISettingRepository settings)
    {
        public static string SettingName(string module, TenantId tenantId) => "DemoContent:" + module + ":" + tenantId.Value.ToString("N");

        public async Task<bool> IsAppliedAsync(TenantId tenantId, int siteId, string module, string version, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var setting = await settings.GetSettingAsync(EntityNames.Site, siteId, SettingName(module, tenantId));
            return setting is not null && string.Equals(setting.SettingValue, version, StringComparison.Ordinal);
        }

        public async Task MarkAppliedAsync(TenantId tenantId, int siteId, string module, string version, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await settings.GetSettingAsync(EntityNames.Site, siteId, SettingName(module, tenantId));
            if (existing is null)
            {
                await settings.AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = siteId, SettingName = SettingName(module, tenantId), SettingValue = version, IsPrivate = true });
            }
            else
            {
                existing.SettingValue = version;
                await settings.UpdateSettingAsync(existing);
            }
        }
    }
}
