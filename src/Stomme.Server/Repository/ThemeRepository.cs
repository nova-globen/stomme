using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.Themes;

namespace Stomme.Repository
{
    public interface IThemeRepository
    {
        Task<IEnumerable<Theme>> GetThemesAsync(int siteId);
        Task<Theme> GetThemeAsync(int themeId, int siteId);
        Task UpdateThemeAsync(Theme theme);
        Task DeleteThemeAsync(int themeId);
        Task<List<string>> GetAssembliesAsync(int siteId);
    }

    /// <summary>
    /// The Theme cluster on the persistence seam (D52) — the MASTER half of the master-bookkeeping wave.
    /// Theme-table access goes through <see cref="IThemeStore"/> (writes) and the MASTER read path
    /// <see cref="IMasterStoreSessionFactory"/> (reads); this class holds no <c>DbContext</c> and no
    /// <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MASTER, not tenant (D23's master half): Theme is installation-wide, so reads go through
    /// <see cref="IMasterStoreSessionFactory"/> — NOT the tenant <see cref="IFrameworkStoreSessionFactory"/> —
    /// and writes through a store bound to <see cref="MasterDBContext"/>. The Theme table persists only four
    /// columns (theme_name, name, the version string and the audit quartet); the rest of the <see cref="Theme"/>
    /// contract is rebuilt here from the loaded theme assemblies and from Settings/Permissions.
    /// </para>
    /// <para>
    /// Reads NEVER go through the store (D19): a query session opens its own context, so the store's change
    /// tracker stays clean for the read-then-write theme-sync pass (read the DB themes through a session, then
    /// Add/Update/Delete through the store). The one read through the store is <see cref="DeleteThemeAsync"/>,
    /// which deletes the very instance it loaded — tracking is what we want there.
    /// </para>
    /// </remarks>
    public class ThemeRepository : IThemeRepository
    {
        private readonly IThemeStore _store;
        private readonly IMasterStoreSessionFactory _sessions;
        private readonly ICacheManager _cache;
        private readonly IPermissionRepository _permissions;
        private readonly ITenantManager _tenants;
        private readonly ISettingRepository _settings;
        private readonly string settingprefix = "SiteEnabled:";

        public ThemeRepository(IThemeStore store, IMasterStoreSessionFactory sessions, ICacheManager cache, IPermissionRepository permissions, ITenantManager tenants, ISettingRepository settings)
        {
            _store = store;
            _sessions = sessions;
            _cache = cache;
            _permissions = permissions;
            _tenants = tenants;
            _settings = settings;
        }

        // The read path on the seam (D19): reads go through a MASTER query session, never the store, so the
        // store's tracker stays clean for the read-then-write theme-sync pass. The IQueryable is materialized
        // inside this method, so it never leaves the repository (D7). Plain LINQ rather than EF's async
        // operators, which would bind these reads to the relational provider.
        private async Task<T> QueryAsync<T>(Func<IQuerySession, T> read)
        {
            var session = _sessions.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<IEnumerable<Theme>> GetThemesAsync(int siteId)
        {
            return await LoadThemesAsync(siteId);
        }

        public async Task<Theme> GetThemeAsync(int themeId, int siteId)
        {
            List<Theme> themes = await LoadThemesAsync(siteId);
            return themes.Find(item => item.ThemeId == themeId);
        }

        public async Task UpdateThemeAsync(Theme theme)
        {
            // The harvested code set _db.Entry(theme).State = Modified and saved; on the seam that is an Update
            // through the store, which attaches the mapped columns (ThemeName/Name/Version/audit) as Modified.
            // DbContextUtils re-stamps ModifiedBy/On and preserves the CreatedBy/On the theme round-tripped.
            await _store.UpdateAsync(ThemeMapper.ToAggregate(theme, assignId: true));
            await _permissions.UpdatePermissionsAsync(theme.SiteId, EntityNames.Theme, theme.ThemeId, theme.PermissionList);

            var settingname = $"{settingprefix}{(await _tenants.GetAliasAsync()).SiteKey}";
            var setting = await _settings.GetSettingAsync(EntityNames.Theme, theme.ThemeId, settingname);
            if (setting == null)
            {
                await _settings.AddSettingAsync(new Setting { EntityName = EntityNames.Theme, EntityId = theme.ThemeId, SettingName = settingname, SettingValue = theme.IsEnabled.ToString(), IsPrivate = true });
            }
            else
            {
                setting.SettingValue = theme.IsEnabled.ToString();
                await _settings.UpdateSettingAsync(setting);
            }

            await _cache.RemoveCacheAsync(await _tenants.GetAliasAsync(), "Themes");
        }

        public async Task DeleteThemeAsync(int themeId)
        {
            // The one read that legitimately goes through the store (D19): it deletes the very instance it loaded,
            // so tracking is what we want and no second instance is ever attached. The harvested code threw a
            // NullReferenceException for an unknown id; returning silently matches DeleteJob/DeleteSetting's shape.
            var aggregate = await _store.GetByIdAsync(themeId);
            await _settings.DeleteSettingsAsync(EntityNames.Theme, themeId);
            if (aggregate != null)
            {
                await _store.DeleteAsync(aggregate);
            }
            await _cache.RemoveCacheAsync(await _tenants.GetAliasAsync(), "Themes");
        }

        public async Task<List<string>> GetAssembliesAsync(int siteId)
        {
            var assemblies = new List<string>();
            foreach (var moduleDefinition in await GetThemesAsync(siteId))
            {
                if (moduleDefinition.IsEnabled)
                {
                    // build list of assemblies for site
                    if (!assemblies.Contains(moduleDefinition.AssemblyName))
                    {
                        assemblies.Add(moduleDefinition.AssemblyName);
                    }
                    if (!string.IsNullOrEmpty(moduleDefinition.Dependencies))
                    {
                        foreach (var assembly in moduleDefinition.Dependencies.Replace(".dll", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Reverse())
                        {
                            if (!assemblies.Contains(assembly.Trim()))
                            {
                                assemblies.Insert(0, assembly.Trim());
                            }
                        }
                    }
                }
            }
            return assemblies;
        }

        private async Task<List<Theme>> LoadThemesAsync(int siteId)
        {
            // get themes
            List<Theme> themes = await _cache.GetCacheAsync(await _tenants.GetAliasAsync(), "Themes", async entry =>
            {
                return await ProcessThemesAsync(siteId);
            }, TimeSpan.MaxValue, TimeSpan.MinValue); // skip distributed caching as app restart must reload themes

            return themes;
        }

        private async Task<List<Theme>> ProcessThemesAsync(int siteId)
        {
            // get themes
            List<Theme> Themes = LoadThemesFromAssemblies();

            // get themes in database — read through a MASTER query session (untracked, D19), mapped to the
            // Theme contract so the sync algorithm below is unchanged from the harvested code.
            List<Theme> themes = (await QueryAsync(session => session.Query<ThemeAggregate>().ToList())).ConvertAll(ThemeMapper.ToModel);

            // sync theme assemblies with database
            foreach (Theme Theme in Themes)
            {
                Theme theme = themes.Where(item => item.ThemeName == Theme.ThemeName).FirstOrDefault();
                if (theme == null)
                {
                    // new theme — insert through the store, then copy the generated id and the DbContextUtils
                    // audit stamps back onto the model, which the "load db properties" block below reads.
                    theme = new Theme { ThemeName = Theme.ThemeName, Version = Theme.Version };
                    var aggregate = ThemeMapper.ToAggregate(theme, assignId: false);
                    await _store.AddAsync(aggregate);
                    ThemeMapper.CopyPersistedValues(aggregate, theme);
                }
                else
                {
                    if (theme.Version != Theme.Version)
                    {
                        // update theme version through the store (D19: the row was read via the session, so a
                        // fresh aggregate is attached rather than a tracked instance mutated).
                        theme.Version = Theme.Version;
                        await _store.UpdateAsync(ThemeMapper.ToAggregate(theme, assignId: true));
                    }

                    // override user customizable property values
                    Theme.Name = (!string.IsNullOrEmpty(theme.Name)) ? theme.Name : Theme.Name;

                    // remove theme from list as it is already synced
                    themes.Remove(theme);
                }

                // format theme control names
                foreach (var themecontrol in Theme.Themes)
                {
                    themecontrol.Name = Theme.Name + " - " + themecontrol.Name;
                }

                // load db properties
                Theme.ThemeId = theme.ThemeId;
                Theme.CreatedBy = theme.CreatedBy;
                Theme.CreatedOn = theme.CreatedOn;
                Theme.ModifiedBy = theme.ModifiedBy;
                Theme.ModifiedOn = theme.ModifiedOn;
                Theme.Fingerprint = Stomme.Infrastructure.ResourceFingerprint.Of(theme.ModifiedOn, Theme.Version);
            }

            // any remaining themes are orphans
            foreach (Theme theme in themes)
            {
                // delete through the store (D19: attach a fresh aggregate, since the row was read via session).
                await _store.DeleteAsync(ThemeMapper.ToAggregate(theme, assignId: true));
            }

            if (siteId != -1)
            {
                var siteKey = (await _tenants.GetAliasAsync()).SiteKey;

                // get all module definition permissions for site
                List<Permission> permissions = (await _permissions.GetPermissionsAsync(siteId, EntityNames.Theme)).ToList();

                // get settings for site
                var settings = (await _settings.GetSettingsAsync(EntityNames.Theme)).ToList();

                // populate theme site settings
                foreach (Theme theme in Themes)
                {
                    theme.SiteId = siteId;

                    var setting = settings.FirstOrDefault(item => item.EntityId == theme.ThemeId && item.SettingName == $"{settingprefix}{siteKey}");
                    if (setting != null)
                    {
                        theme.IsEnabled = bool.Parse(setting.SettingValue);
                    }
                    else
                    {
                        theme.IsEnabled = theme.IsAutoEnabled;
                    }

                    if (permissions.Count == 0)
                    {
                        // no module definition permissions exist for this site
                        theme.PermissionList = ClonePermissions(siteId, theme.PermissionList);
                        await _permissions.UpdatePermissionsAsync(siteId, EntityNames.Theme, theme.ThemeId, theme.PermissionList);
                    }
                    else
                    {
                        if (permissions.Any(item => item.EntityId == theme.ThemeId))
                        {
                            theme.PermissionList = permissions.Where(item => item.EntityId == theme.ThemeId).ToList();
                        }
                        else
                        {
                            // permissions for theme do not exist for this site
                            theme.PermissionList = ClonePermissions(siteId, theme.PermissionList);
                            await _permissions.UpdatePermissionsAsync(siteId, EntityNames.Theme, theme.ThemeId, theme.PermissionList);
                        }
                    }
                }

                // clean up any orphaned permissions
                var ids = new HashSet<int>(Themes.Select(item => item.ThemeId));
                foreach (var permission in permissions.Where(item => !ids.Contains(item.EntityId)))
                {
                    try
                    {
                        await _permissions.DeletePermissionAsync(permission.PermissionId);
                    }
                    catch
                    {
                        // multi-threading can cause a race condition to occur
                    }
                }
            }

            return Themes;
        }

        private List<Theme> LoadThemesFromAssemblies()
        {
            List<Theme> themes = new List<Theme>();

            // iterate through Stomme theme assemblies
            var assemblies = AppDomain.CurrentDomain.GetStommeAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                if (System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), Utilities.GetTypeName(assembly.FullName) + ".dll")))
                {
                    themes = LoadThemesFromAssembly(themes, assembly);
                }
            }

            return themes;
        }

        private List<Theme> LoadThemesFromAssembly(List<Theme> themes, Assembly assembly)
        {
            Theme theme;

            Type[] themeTypes = assembly.GetTypes().Where(item => item.GetInterfaces().Contains(typeof(ITheme))).ToArray();
            Type[] themeControlTypes = assembly.GetTypes().Where(item => item.GetInterfaces().Contains(typeof(IThemeControl))).ToArray();
            Type[] containerControlTypes = assembly.GetTypes().Where(item => item.GetInterfaces().Contains(typeof(IContainerControl))).ToArray();

            foreach (Type themeControlType in themeControlTypes)
            {
                // Check if type should be ignored
                if (themeControlType.IsStommeIgnore() || 
                    themeControlType.GetInterfaces().Contains(typeof(ILayoutControl)) || // deprecated 
                    themeControlType.GetInterfaces().Contains(typeof(IContainerControl))) continue;

                // create namespace root typename
                string qualifiedThemeType = themeControlType.Namespace + ", " + themeControlType.Assembly.GetName().Name;

                int index = themes.FindIndex(item => item.ThemeName == qualifiedThemeType);
                if (index == -1)
                {
                    // determine if this component is part of a theme which implements ITheme
                    Type themetype = themeTypes.FirstOrDefault(item => item.Namespace == themeControlType.Namespace);

                    if (themetype != null)
                    {
                        var themeobject = Activator.CreateInstance(themetype) as ITheme;
                        theme = themeobject.Theme;
                    }
                    else
                    {
                        theme = new Theme
                        {
                            Name = themeControlType.Name,
                            Version = new Version(1, 0, 0).ToString()
                        };
                    }

                    // set internal properties
                    theme.ThemeName = qualifiedThemeType;
                    theme.Themes = new List<ThemeControl>();
                    theme.Containers = new List<ThemeControl>();
                    theme.AssemblyName = assembly.FullName.Split(",")[0];
                    if (theme.Resources != null)
                    {
                        foreach (var resource in theme.Resources)
                        {
                            if (!string.IsNullOrEmpty(resource.Url) && resource.Url.StartsWith("~"))
                            {
                                resource.Url = resource.Url.Replace("~", "/Themes/" + Utilities.GetTypeName(theme.ThemeName) + "/").Replace("//", "/");
                            }
                        }
                    }

                    // default permissions
                    theme.PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.Utilize, RoleNames.Admin, true),
                        new Permission(PermissionNames.Utilize, RoleNames.Registered, true)
                    };

                    Debug.WriteLine($"Stomme Info: Registering Theme {theme.ThemeName}");
                    themes.Add(theme);
                    index = themes.FindIndex(item => item.ThemeName == qualifiedThemeType);
                }
                theme = themes[index];

                // add theme control
                var themecontrolobject = Activator.CreateInstance(themeControlType) as IThemeControl;
                theme.Themes.Add(
                    new ThemeControl
                    {
                        TypeName = themeControlType.FullName + ", " + themeControlType.Assembly.GetName().Name,
                        Name = ((string.IsNullOrEmpty(themecontrolobject.Name)) ? Utilities.GetTypeNameLastSegment(themeControlType.FullName, 0) : themecontrolobject.Name),
                        Thumbnail = themecontrolobject.Thumbnail,
                        Panes = themecontrolobject.Panes
                    }
                );

                if (!theme.Containers.Any())
                {
                    // add container controls
                    foreach (Type containertype in containerControlTypes.Where(item => item.Namespace == themeControlType.Namespace))
                    {
                        var containerobject = Activator.CreateInstance(containertype) as IThemeControl;
                        theme.Containers.Add(
                            new ThemeControl
                            {
                                TypeName = containertype.FullName + ", " + themeControlType.Assembly.GetName().Name,
                                Name = (string.IsNullOrEmpty(containerobject.Name)) ? Utilities.GetTypeNameLastSegment(containertype.FullName, 0) : containerobject.Name,
                                Thumbnail = containerobject.Thumbnail,
                                Panes = ""
                            }
                        );
                    }
                }

                themes[index] = theme;
            }
            return themes;
        }

        private List<Permission> ClonePermissions(int siteId, List<Permission> permissionList)
        {
            var permissions = new List<Permission>();
            if (permissionList != null)
            {
                foreach (var p in permissionList)
                {
                    var permission = new Permission();
                    permission.SiteId = siteId;
                    permission.EntityName = p.EntityName;
                    permission.EntityId = p.EntityId;
                    permission.PermissionName = p.PermissionName;
                    permission.RoleId = null;
                    permission.RoleName = p.RoleName;
                    permission.UserId = p.UserId;
                    permission.IsAuthorized = p.IsAuthorized;
                    permissions.Add(permission);
                }
            }
            return permissions;
        }
    }

    /// <summary>
    /// The pure half of the Theme surface: projection over themes the caller has ALREADY fetched.
    /// </summary>
    /// <remarks>
    /// <c>FilterThemes</c> lives here rather than on <see cref="IThemeRepository"/> because it is an
    /// in-memory copy of a list the caller passes in — it performs no I/O and has nothing to await. D32 bans
    /// sync members on store-touching interfaces; the honest response for genuinely synchronous work is to
    /// take it OFF the store-touching interface, not to wrap it in <c>Task.FromResult</c> and call it async.
    /// An extension on <see cref="IThemeRepository"/> keeps the existing call site
    /// (<c>_themes.FilterThemes(...)</c>) compiling unchanged.
    /// </remarks>
    public static class ThemeRepositoryExtensions
    {
        /// <summary>
        /// Returns copies of the enabled themes carrying only the client-side properties.
        /// </summary>
        public static List<Theme> FilterThemes(this IThemeRepository repository, List<Theme> themes)
        {
            var Themes = new List<Theme>();

            foreach (Theme theme in themes.Where(item => item.IsEnabled))
            {
                var Theme = new Theme();
                Theme.ThemeName = theme.ThemeName;
                Theme.Name = theme.Name;
                Theme.Resources = theme.Resources;
                Theme.Themes = theme.Themes;
                Theme.Containers = theme.Containers;
                Theme.ThemeSettingsType = theme.ThemeSettingsType;
                Theme.ContainerSettingsType = theme.ContainerSettingsType;
                Theme.PackageName = theme.PackageName;
                Theme.ColorMode = theme.ColorMode;
                Theme.PermissionList = theme.PermissionList;
                Theme.Fingerprint = theme.Fingerprint;
                Themes.Add(Theme);
            }

            return Themes;
        }
    }
}
