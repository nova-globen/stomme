#nullable enable
using System;
using System.Globalization;

namespace Stomme.Modules.Pipelines.Tenancy
{
    /// <summary>
    /// The engine's tenant is a Stomme SITE (BL-95 increment (a), the tenant bridge). Pipelines are authored and
    /// run per site - a site is what an admin page belongs to and what a job iterates - while the engine's store
    /// database is shared by every site of an installation (the relational sidecar) or of a Stomme tenant (the
    /// RavenDB routing), so the id has to name the site UNIQUELY across Stomme tenants: it is
    /// <c>"{Stomme TenantId}/{SiteId}"</c>, both integers, stable for the site's life.
    /// </summary>
    public static class EngineTenantIds
    {
        public static string For(int tenantId, int siteId) =>
            string.Create(CultureInfo.InvariantCulture, $"{tenantId}/{siteId}");

        public static bool TryParse(string? engineTenantId, out int tenantId, out int siteId)
        {
            tenantId = 0;
            siteId = 0;
            if (string.IsNullOrEmpty(engineTenantId))
            {
                return false;
            }

            var separator = engineTenantId.IndexOf('/', StringComparison.Ordinal);
            return separator > 0
                && int.TryParse(engineTenantId.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out tenantId)
                && int.TryParse(engineTenantId.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out siteId);
        }
    }
}
