using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using Stomme.Interfaces;
using Stomme.Providers;
using Stomme.Services;
using Stomme.Shared;
using Microsoft.FluentUI.AspNetCore.Components;
using IThemeService = Stomme.Services.IThemeService; // FluentUI 5 brings its own two of these names
using INotificationService = Stomme.Services.INotificationService;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class StommeServiceCollectionExtensions
    {
        public static IServiceCollection AddStommeAuthentication(this IServiceCollection services)
        {
            services.AddAuthorizationCore();
            services.AddCascadingAuthenticationState();
            services.AddScoped<IdentityAuthenticationStateProvider>();
            services.AddScoped<AuthenticationStateProvider>(s => s.GetRequiredService<IdentityAuthenticationStateProvider>());

            return services;
        }

        public static IServiceCollection AddStommeClientScopedServices(this IServiceCollection services)
        {
            services.AddScoped<SiteState>();
            services.AddScoped<Stomme.Services.ThemeState>();
            services.AddScoped<IInstallationService, InstallationService>();
            services.AddScoped<IModuleDefinitionService, ModuleDefinitionService>();
            services.AddScoped<IThemeService, Stomme.Services.ThemeService>();
            services.AddScoped<IAliasService, AliasService>();
            services.AddScoped<ITenantService, TenantService>();
            services.AddScoped<ISiteService, SiteService>();
            services.AddScoped<IPageService, PageService>();
            services.AddScoped<IModuleService, ModuleService>();
            services.AddScoped<IPageModuleService, PageModuleService>();
            services.AddScoped<IUserService, UserService>();
            services.AddScoped<IProfileService, ProfileService>();
            services.AddScoped<IRoleService, RoleService>();
            services.AddScoped<IUserRoleService, UserRoleService>();
            services.AddScoped<ISettingService, SettingService>();
            services.AddScoped<IPackageService, PackageService>();
            services.AddScoped<ILogService, LogService>();
            services.AddScoped<IJobService, JobService>();
            services.AddScoped<IJobLogService, JobLogService>();
            services.AddScoped<INotificationService, Stomme.Services.NotificationService>();
            services.AddScoped<IFolderService, FolderService>();
            services.AddScoped<IFileService, FileService>();
            services.AddScoped<ISiteTemplateService, SiteTemplateService>();
            services.AddScoped<ISqlService, SqlService>();
            services.AddScoped<ISystemService, SystemService>();
            services.AddScoped<ILocalizationService, LocalizationService>();
            services.AddScoped<ILanguageService, LanguageService>();
            services.AddScoped<IDatabaseService, DatabaseService>();
            services.AddScoped<IUrlMappingService, UrlMappingService>();
            services.AddScoped<IVisitorService, VisitorService>();
            services.AddScoped<ISyncService, SyncService>();
            services.AddScoped<ILocalizationCookieService, LocalizationCookieService>();
            services.AddScoped<ICookieConsentService, CookieConsentService>();
            services.AddScoped<ITimeZoneService, TimeZoneService>();
            services.AddScoped<IMigrationHistoryService, MigrationHistoryService>();
            services.AddScoped<ISiteGroupService, SiteGroupService>();
            services.AddScoped<ISiteGroupMemberService, SiteGroupMemberService>();
            services.AddScoped<ISiteTaskService, SiteTaskService>();
            services.AddScoped<IOutputCacheService, OutputCacheService>();
            services.AddScoped<IOpenIddictService, OpenIddictService>();
            services.AddScoped<ILicenceService, LicenceService>();
            services.AddScoped<IFeatureService, FeatureService>();
            services.AddScoped<IDeadLetterService, DeadLetterService>();

            // providers
            services.AddScoped<ITextEditor, Stomme.Modules.Controls.QuillJSTextEditor>();
            services.AddScoped<ITextEditor, Stomme.Modules.Controls.TextAreaTextEditor>();

            services.AddFluentUIComponents();

            return services;
        }
    }
}
