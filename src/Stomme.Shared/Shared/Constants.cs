using System;
using System.Reflection;

namespace Stomme.Shared
{
    public class Constants
    {
        /// <summary>
        /// The framework's release number: what the release ledger, the installed version, a module's version and every
        /// framework-version check compare, so it is always numeric. A preview package of a release carries that release's
        /// number here and its label in <see cref="PackageVersion"/>.
        /// </summary>
        public static readonly string Version = "10.75.3";

        /// <summary>
        /// The version this build's packages carry on the feed: <see cref="Version"/>, plus a prerelease label on a preview
        /// (<c>10.5.0-preview.1</c>). Read from the build (<c>Directory.Build.props</c>' <c>Version</c>), so it cannot drift;
        /// use it where a package is named - a generated project's package references, the version a page reports.
        /// </summary>
        public static readonly string PackageVersion = ReadPackageVersion();

        private static string ReadPackageVersion()
        {
            var informational = typeof(Constants).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(informational))
            {
                return Version;
            }

            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational.Substring(0, plus) : informational;
        }

        public const string ReleaseVersions = "1.0.0,1.0.1,1.0.2,1.0.3,1.0.4,2.0.0,2.0.1,2.0.2,2.1.0,2.2.0,2.3.0,2.3.1,3.0.0,3.0.1,3.0.2,3.0.3,3.1.0,3.1.1,3.1.2,3.1.3,3.1.4,3.2.0,3.2.1,3.3.0,3.3.1,3.4.0,3.4.1,3.4.2,3.4.3,4.0.0,4.0.1,4.0.2,4.0.3,4.0.4,4.0.5,4.0.6,5.0.0,5.0.1,5.0.2,5.0.3,5.1.0,5.1.1,5.1.2,5.2.0,5.2.1,5.2.2,5.2.3,5.2.4,6.0.0,6.0.1,6.1.0,6.1.1,6.1.2,6.1.3,6.1.4,6.1.5,6.2.0,6.2.1,10.0.0,10.0.1,10.0.2,10.0.3,10.0.4,10.1.0,10.1.1,10.1.2,10.2.0,10.2.1,10.2.2,10.3.0,10.4.0,10.4.1,10.5.0,10.5.1,10.5.2,10.6.0,10.7.0,10.8.0,10.9.0,10.10.0,10.11.0,10.12.0,10.13.0,10.14.0,10.15.0,10.16.0,10.17.0,10.18.0,10.19.0,10.20.0,10.21.0,10.22.0,10.23.0,10.24.0,10.25.0,10.26.0,10.27.0,10.28.0,10.29.0,10.30.0,10.31.0,10.32.0,10.33.0,10.34.0,10.35.0,10.36.0,10.37.0,10.38.0,10.39.0,10.40.0,10.41.0,10.42.0,10.43.0,10.44.0,10.45.0,10.46.0,10.47.0,10.48.0,10.49.0,10.50.0,10.51.0,10.52.0,10.53.0,10.54.0,10.55.0,10.56.0,10.57.0,10.58.0,10.59.0,10.60.0,10.61.0,10.62.0,10.63.0,10.64.0,10.65.0,10.66.0,10.67.0,10.68.0,10.69.0,10.70.0,10.71.0,10.72.0,10.73.0,10.73.1,10.73.2,10.74.0,10.74.1,10.74.2,10.75.0,10.75.1,10.75.2,10.75.3";
        public const string PackageId = "Stomme.Framework";
        public const string ClientId = "Stomme.Client";
        public const string UpdaterPackageId = "Stomme.Updater";
        // no package registry by default: the marketplace lists nothing until an install sets the PackageRegistryUrl setting
        public const string PackageRegistryUrl = "";

        public const string AppSettingsOverrideEnvironmentVariable = "STOMME_APPSETTINGS_PATH";

        public const string DataDirectory = "DataDirectory";
        public const string DefaultDBName = "LocalDB";
        public const string DefaultDBType = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server";

        // Graphite is the default Stomme theme: near-monochrome, enterprise, indigo accent.
        // StommeTheme (amber) and BlazorTheme remain discoverable/selectable.
        public const string DefaultTheme = "Stomme.Themes.GraphiteTheme.Default, Stomme.Client";
        public const string DefaultContainer = "Stomme.Themes.GraphiteTheme.Container, Stomme.Client";
        public const string DefaultAdminContainer = "Stomme.Themes.AdminContainer, Stomme.Client";

        public const string ActionToken = "{Action}";
        public const string DefaultAction = "Index";

        // "hubs": a module's SignalR hub at /hubs/<name> answers under a folder site too (/lek/hubs/<name>), the
        // folder's prefix stripped and the site resolved; so no site or top-level page may be named "hubs" either.
        // "_blazor": a folder site's interactive circuit connects at /<folder>/_blazor, the folder's prefix stripped, so the
        // circuit resolves the folder site and carries its cookies.
        public static readonly string[] ReservedRoutes = { "api", "pages", "files", "hubs", "_blazor" };
        public const string ModuleDelimiter = "*";
        public const string UrlParametersDelimiter = "!";

        // Default Module Actions are reserved and should not be used by modules
        public static readonly string[] DefaultModuleActions = new[] { "Settings", "Import", "Export" };
        public static readonly string DefaultModuleActionsTemplate = "Stomme.Modules.Admin.Modules." + ActionToken + ", Stomme.Client";

        public const string AdminDashboardModule = "Stomme.Modules.Admin.Dashboard, Stomme.Client";
        public const string PageManagementModule = "Stomme.Modules.Admin.Pages, Stomme.Client";
        public const string ErrorModule = "Stomme.Modules.Admin.Error.{Action}, Stomme.Client";

        public const string AdminSiteTemplate = "Stomme.Infrastructure.SiteTemplates.AdminSiteTemplate, Stomme.Server";
        public const string DefaultSiteTemplate = "Stomme.Infrastructure.SiteTemplates.DefaultSiteTemplate, Stomme.Server";
        public const string EmptySiteTemplate = "Stomme.Infrastructure.SiteTemplates.EmptySiteTemplate, Stomme.Server";

        // Admin modules whose default Utilize permission is HOST rather than Admin. "OpenIddict" is here because
        // registering an OAuth client mints credentials against the tenant's authority — a host-level act, the
        // same posture the OpenIddictController's [Authorize(Roles = Host)] takes on every action.
        public static readonly string[] DefaultHostModuleTypes = new[] { "Upgrade", "Themes", "SystemInfo", "Sql", "Sites", "ModuleDefinitions", "Logs", "Jobs", "ModuleCreator", "OpenIddict" };

        public const string FileUrl = "/files/";
        public const string ImageUrl = "/api/file/image/";
        public const int UserFolderCapacity = 20; // megabytes
        public const string PackagesFolder = "Packages";

        public const string DefaultSite = "Default Site";

        public const string ImageFiles = "jpg,jpeg,jpe,gif,bmp,png,ico,webp";
        public const string UploadableFiles = ImageFiles + ",mov,wmv,avi,mp4,mp3,doc,docx,xls,xlsx,ppt,pptx,pdf,txt,zip,nupkg,csv,json,rss,css,md";
        public const string ReservedDevices = "CON,NUL,PRN,COM0,COM1,COM2,COM3,COM4,COM5,COM6,COM7,COM8,COM9,LPT0,LPT1,LPT2,LPT3,LPT4,LPT5,LPT6,LPT7,LPT8,LPT9,CONIN$,CONOUT$";

        public static readonly char[] InvalidFileNameChars =
        {
            '\"', '<', '>', '|', '\0', (Char) 1, (Char) 2, (Char) 3, (Char) 4, (Char) 5, (Char) 6, (Char) 7, (Char) 8,
            (Char) 9, (Char) 10, (Char) 11, (Char) 12, (Char) 13, (Char) 14, (Char) 15, (Char) 16, (Char) 17, (Char) 18,
            (Char) 19, (Char) 20, (Char) 21, (Char) 22, (Char) 23, (Char) 24, (Char) 25, (Char) 26, (Char) 27,
            (Char) 28, (Char) 29, (Char) 30, (Char) 31, ':', '*', '?', '\\', '/'
        };
        public static readonly string[] InvalidFileNameEndingChars = { ".", " " };

        public static readonly string SatelliteAssemblyExtension = ".resources.dll";

        public static readonly string DefaultCulture = "en";

        public static readonly string AuthenticationScheme = "Identity.Application";
        public static readonly string RequestVerificationToken = "__RequestVerificationToken";
        public static readonly string AntiForgeryTokenHeaderName = "X-XSRF-TOKEN-HEADER";
        public static readonly string AntiForgeryTokenCookieName = "X-XSRF-TOKEN-COOKIE";

        public static readonly string SecurityStampClaimType = "AspNet.Identity.SecurityStamp";
        public static readonly string SiteKeyClaimType = "Stomme.Identity.SiteKey";
        /// <summary>The server-side session an authentication cookie belongs to - a Guid, revocable one at a time.</summary>
        public static readonly string SessionClaimType = "Stomme.Identity.Session";

        public static readonly string DefaultVisitorFilter = "bot,crawler,slurp,spider,(none),??";

        public static readonly string HttpContextAliasKey = "Alias";
        public static readonly string HttpContextSiteSettingsKey = "SiteSettings";

        public static readonly string MauiUserAgent = "MAUI";
        public static readonly string MauiAliasPath = "Alias-Path";
        public const string MauiCorsPolicy = "MauiCorsPolicy"; // must be a constant to be used with an attribute

        public static readonly string VisitorCookiePrefix = "APP_VISITOR_";

        public const string DefaultSearchProviderName = "DatabaseSearchProvider";

        public static readonly string[] InternalPagePaths = { "login", "register", "reset", "404" };

        public const string DefaultTextEditor = "Stomme.Modules.Controls.QuillJSTextEditor, Stomme.Client";

        // obtained from https://cdnjs.com/libraries/bootstrap
        public const string BootstrapScriptUrl = "https://cdnjs.cloudflare.com/ajax/libs/bootstrap/5.3.8/js/bootstrap.bundle.min.js";
        public const string BootstrapScriptIntegrity = "sha512-HvOjJrdwNpDbkGJIG2ZNqDlVqMo77qbs4Me4cah0HoDrfhrbA+8SBlZn1KrvAQw7cILLPFJvdwIgphzQmMm+Pw==";
        public const string BootstrapStylesheetUrl = "https://cdnjs.cloudflare.com/ajax/libs/bootstrap/5.3.8/css/bootstrap.min.css";
        public const string BootstrapStylesheetIntegrity = "sha512-2bBQCjcnw658Lho4nlXJcc6WkV/UxpE/sAokbXPxQNGqmNdQrWqtw26Ns9kFF/yG792pKR1Sx8/Y1Lf1XN4GKA==";

        public const string CookieConsentCookieName = "Stomme.CookieConsent";
        public const string CookieConsentCookieValue = "yes";
        public const string CookieConsentActionCookieValue = "yes";

        public const string SitemapOutputCacheTag = "Sitemap";
        // Obsolete constants

        const string RoleObsoleteMessage = "Use the corresponding member from Stomme.Shared.RoleNames";

        [Obsolete(RoleObsoleteMessage)]
        public const string AllUsersRole = RoleNames.Everyone;
        [Obsolete(RoleObsoleteMessage)]
        public const string HostRole = RoleNames.Host;
        [Obsolete(RoleObsoleteMessage)]
        public const string AdminRole = RoleNames.Admin;
        [Obsolete(RoleObsoleteMessage)]
        public const string RegisteredRole = RoleNames.Registered;

        [Obsolete("DefaultLayout is deprecated")]
        public const string DefaultLayout = "";

        [Obsolete("Use PaneNames.Admin")]
        public const string AdminPane = PaneNames.Admin;

        [Obsolete("Use UserNames.Host instead.")]
        public const string HostUser = UserNames.Host;

        [Obsolete("Use TenantNames.Master instead")]
        public const string MasterTenant = TenantNames.Master;

        // [Obsolete("Use FileUrl instead")]
        public const string ContentUrl = "/api/file/download/";
    }
}
