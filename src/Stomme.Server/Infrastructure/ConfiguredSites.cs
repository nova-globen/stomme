using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// One entry of <c>Installation:Sites</c>: a site the host creates at boot, after the first site, if it does not exist.
    /// </summary>
    /// <remarks>
    /// Every property is a plain string, so the list is expressible as environment variables alone
    /// (<c>Installation__Sites__0__Name</c>, <c>Installation__Sites__0__Aliases</c>, ...). Without <c>Tenant</c> the site
    /// joins the installation's own (Master) tenant; with <c>Tenant</c> and <c>ConnectionString</c> it gets a new tenant
    /// with a database of its own; with <c>Tenant</c> alone it joins that existing tenant.
    /// </remarks>
    public sealed class ConfiguredSite
    {
        public string Name { get; set; }
        public string Aliases { get; set; }
        public string Tenant { get; set; }
        public string ConnectionString { get; set; }
        public string DatabaseType { get; set; }
        public string SiteTemplate { get; set; }
        public string DefaultTheme { get; set; }
        public string DefaultContainer { get; set; }
        public string RenderMode { get; set; }
        public string Runtime { get; set; }

        internal string[] AliasNames =>
            (Aliases ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(alias => alias.Trim()).Where(alias => alias.Length > 0).ToArray();

        internal string TenantName => string.IsNullOrWhiteSpace(Tenant) ? TenantNames.Master : Tenant.Trim();

        internal bool CreatesTenant => !string.IsNullOrWhiteSpace(ConnectionString);
    }

    /// <summary>What the boot knows about the installation when it checks <c>Installation:Sites</c>.</summary>
    internal sealed record ConfiguredSitesState(
        IReadOnlyCollection<(string Name, int SiteId)> Aliases,
        IReadOnlyCollection<string> Tenants,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> SiteNamesByTenant,
        bool HostPasswordAvailable,
        string DataDirectory,
        string DefaultDatabaseType);

    /// <summary>
    /// Checks <c>Installation:Sites</c> as a WHOLE before anything is written: an entry whose first alias already names a
    /// site is done and skipped; every problem in the rest is named, and any problem refuses the boot with nothing created.
    /// </summary>
    internal static class ConfiguredSites
    {
        public const string Section = SettingKeys.InstallationSection + ":Sites";

        /// <summary>
        /// <c>ConnectionStrings:&lt;Tenant&gt;</c> for every entry that names a tenant and a connection string - the name a
        /// created tenant resolves its database by. <c>Program</c> adds these beneath every other configuration source.
        /// </summary>
        public static Dictionary<string, string> ConnectionStrings(IConfiguration configuration)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in configuration.GetSection(Section).GetChildren())
            {
                var tenant = entry["Tenant"]?.Trim();
                var connectionString = entry["ConnectionString"];
                if (!string.IsNullOrEmpty(tenant) && !string.IsNullOrWhiteSpace(connectionString))
                {
                    result[$"{SettingKeys.ConnectionStringsSection}:{tenant}"] = connectionString;
                }
            }
            return result;
        }

        public static (List<(int Index, ConfiguredSite Site)> ToCreate, List<string> Problems) Plan(
            IReadOnlyList<ConfiguredSite> entries, ConfiguredSitesState state)
        {
            var toCreate = new List<(int, ConfiguredSite)>();
            var problems = new List<string>();
            var aliasOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tenantsCreatedEarlier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var at = $"{Section}:{index}";
                var aliases = entry.AliasNames;

                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    problems.Add($"{at}: Name is required.");
                }
                if (aliases.Length == 0)
                {
                    problems.Add($"{at}: Aliases is required (one or more host names, comma-separated).");
                    continue;
                }

                foreach (var alias in aliases)
                {
                    if (aliasOwners.TryGetValue(alias, out var other))
                    {
                        problems.Add($"{at}: the alias '{alias}' is also listed by {Section}:{other}.");
                    }
                    else
                    {
                        aliasOwners[alias] = index;
                    }
                }

                // Done on an earlier boot: its first alias names a site. Nothing about it is checked or changed again.
                if (state.Aliases.Any(row => row.SiteId > 0 && row.Name.Equals(aliases[0], StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (var alias in aliases)
                {
                    if (state.Aliases.Any(row => row.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add($"{at}: the alias '{alias}' already exists on this installation (another site's, or one left by a site that was never completed).");
                    }
                }

                var tenant = entry.TenantName;
                var tenantExists = state.Tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase);
                if (entry.CreatesTenant)
                {
                    if (string.IsNullOrWhiteSpace(entry.Tenant))
                    {
                        problems.Add($"{at}: a ConnectionString needs a Tenant name for the new tenant.");
                    }
                    else if (tenant.Equals(TenantNames.Master, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{at}: the Tenant '{TenantNames.Master}' is the installation's own; give the new tenant another name.");
                    }
                    else if (tenantExists || tenantsCreatedEarlier.Contains(tenant))
                    {
                        problems.Add($"{at}: the Tenant '{tenant}' already exists; leave out the ConnectionString to add the site to it.");
                    }
                    else
                    {
                        if (!state.HostPasswordAvailable)
                        {
                            problems.Add($"{at}: a new tenant needs the host account's password on this boot: set {SettingKeys.InstallationSection}:{SettingKeys.HostPasswordKey} or {SettingKeys.HostPasswordFileKey}.");
                        }
                        var databaseType = string.IsNullOrWhiteSpace(entry.DatabaseType) ? state.DefaultDatabaseType : entry.DatabaseType;
                        if (Type.GetType(databaseType ?? "") == null)
                        {
                            problems.Add($"{at}: the DatabaseType '{databaseType}' is not a database type this host knows.");
                        }
                        else if (databaseType.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                            && UnwritableSqliteFolder(entry.ConnectionString, state.DataDirectory) is { } folder)
                        {
                            problems.Add($"{at}: the SQLite file's folder '{folder}' cannot be written.");
                        }
                        tenantsCreatedEarlier.Add(tenant);
                    }
                }
                else if (!tenantExists && !tenantsCreatedEarlier.Contains(tenant))
                {
                    problems.Add($"{at}: the Tenant '{tenant}' does not exist; give a ConnectionString to create it.");
                }

                if (!string.IsNullOrWhiteSpace(entry.Name) && tenantExists
                    && state.SiteNamesByTenant.TryGetValue(tenant, out var names)
                    && names.Contains(entry.Name.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{at}: the tenant '{tenant}' already has a site named '{entry.Name.Trim()}'.");
                }

                toCreate.Add((index, entry));
            }

            return (toCreate, problems);
        }

        // The folder a SQLite connection string's file lives in, if it cannot be created or written; null when it can.
        // A connection string is never echoed: it may carry a password.
        private static string UnwritableSqliteFolder(string connectionString, string dataDirectory)
        {
            string folder;
            try
            {
                var dataSource = new SqliteConnectionStringBuilder(
                    connectionString.Replace($"|{Constants.DataDirectory}|", dataDirectory ?? "")).DataSource;
                folder = Path.GetDirectoryName(Path.GetFullPath(dataSource)) ?? "";
            }
            catch (Exception)
            {
                return "(the connection string names no file)";
            }

            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ".stomme-write-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return null;
            }
            catch (Exception)
            {
                return folder;
            }
        }
    }
}
