using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using IThemeService = Stomme.Services.IThemeService; // FluentUI 5 brings its own two of these names (BL-192)
using INotificationService = Stomme.Services.INotificationService;
using Stomme.Authorization;
using Stomme.Extensions;
using Stomme.Identity.OpenIddict;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Licensing;
using Stomme.Infrastructure.Security;
using Stomme.Infrastructure.Security.Cryptography;
using Stomme.Infrastructure.Tenancy;
using Stomme.Interfaces;
using Stomme.Managers;
using Stomme.Modules;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.Providers;
using Stomme.Repository;
using Stomme.Repository.Identity;
using Stomme.Repository.Stores;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class StommeServiceCollectionExtensions
    {
        public static IServiceCollection AddStomme(this IServiceCollection services, IConfigurationRoot configuration, IWebHostEnvironment environment)
        {
            // process forwarded headers from the proxies a deployment has - loopback and the private networks unless it names
            // its own (BL-243: believing every caller let one choose its issuer, scheme, client address and alias)
            services.Configure<ForwardedHeadersOptions>(options => Stomme.Infrastructure.Security.ForwardedHeadersTrust.Configure(options, configuration));

            // BL-121: the host's ONE rate limiter - the account endpoints' policies, ON by default. A module that limits its
            // own endpoint (MCP) adds its policy to these options; the middleware is placed once, in UseStomme.
            services.AddAccountRateLimiting(configuration);

            // register localization services
            services.AddLocalization(options => options.ResourcesPath = "Resources");

            services.AddOptions<List<Stomme.Models.Database>>().Bind(configuration.GetSection(SettingKeys.AvailableDatabasesSection));

            // register scoped core services
            services.AddScoped<IAuthorizationHandler, PermissionHandler>()
                // D99 point 9 (BL-99): the handler for a LIFTED PERMISSION KEY policy ("key:resource:operation",
                // written as [RequiresPermissionKey]). Registered beside PermissionHandler, never instead of it —
                // the framework's own EntityName:PermissionName:Roles policies keep their handler. Each handler
                // only ever sees its OWN requirement type, so this adds nothing to a stock install's decision.
                .AddScoped<IAuthorizationHandler, PermissionKeyHandler>()
                .AddStommeServerScopedServices();

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // register singleton scoped core services
            services.AddSingleton(configuration)
                .AddStommeSingletonServices();

            // setup HttpClient for server side in a client side compatible fashion ( with auth cookie )
            services.AddHttpClients();

            // BL-100 F1 (D109): the ONE file-ingestion guard every FileController path runs through, its guarded
            // download client, and the null scanner (TryAdd - a scanner registered before AddStomme wins).
            services.AddStommeFileIngestion();

            // install any modules or themes ( this needs to occur BEFORE the assemblies are loaded into the app domain )
            InstallationManager.InstallPackages(environment.WebRootPath, environment.ContentRootPath);

            // register transient scoped core services
            services.AddStommeTransientServices();

            // tenancy adapters over the persistence seam (D16). MUST precede AddStommeAssemblies: the scan
            // runs the module startups, and Stomme.FeatureManagement's AddFeatureManagement registers a
            // single-tenant ITenantContext fallback with TryAdd — first registration wins, so ours has to be
            // in the collection before that runs.
            services.AddStommeTenancy();

            // The connector catalog (BL-94 increment 3, D110): every IConnector a module startup or a /bin package registers
            // is in it, by type key, and a key published twice refuses the composition when the catalog is first resolved.
            Stomme.Infrastructure.Connectivity.ConnectorCatalogServiceExtensions.AddConnectorCatalog(services);
            // ...and the egress a connector runs with (increment 4): the framework's one outbound guard, with the site's
            // ConnectivityAllowedHosts on every request. Scoped: it reads the site's settings.
            services.AddScoped<Stomme.Infrastructure.Connectivity.IConnectorEgressFactory, Stomme.Infrastructure.Connectivity.SiteSettingConnectorEgressFactory>();

            // the framework store's aggregate clusters that run on the persistence seam (D17). MUST follow
            // AddStommeTenancy (the seam's repositories take ITenantContext).
            services.AddStommeFrameworkStore();

            // D54: the RavenDB provider for the SAME closed store contracts, registered alongside the EF
            // provider above. Its IDocumentStore is built lazily from the persisted connection string, so a
            // relational install never constructs it and never touches RavenDB. SelectFrameworkStoreProviderByConfig
            // then makes the provider actually resolved for each contract a resolve-time decision keyed on the
            // configured database type — necessary because the type is written mid-install, after this container
            // is built (a composition-time branch would only ever see the empty fresh-boot config's relational
            // default). Both extensions still register every twin, so D42's reflective gate stays green.
            // BL-176: on a RavenDB install a tenant's rows live in the database its connection string names (the master
            // tenant's is the installation's own). The host's resolver REPLACES the one-shared-database default that
            // AddRavenFrameworkStore and the module store's Raven registration add with TryAdd.
            services.TryAddSingleton<Stomme.Repository.Stores.RavenTenantDatabases>();
            services.RemoveAll<Stomme.Persistence.RavenDb.IRavenTenantDatabaseResolver>();
            services.AddSingleton<Stomme.Persistence.RavenDb.IRavenTenantDatabaseResolver, Stomme.Repository.Stores.TenantRavenDatabaseResolver>();
            services.AddRavenFrameworkStore(BuildRavenFrameworkDocumentStore);
            services.SelectFrameworkStoreProviderByConfig();

            // The authorization bridge (D99, BL-99): [Requires] on a mediator request is enforced by the
            // AuthorizationBehavior over the row-evaluating PermissionRowAccessGuard. UNCONDITIONAL — never gated on
            // "a lifted module exists" (F4) — and BEFORE AddStommeAssemblies: Mediator.Send reverses the resolved
            // IPipelineBehavior registrations and wraps the handler once per behavior, so the FIRST registered
            // behavior is the OUTERMOST, and the module startups the scan runs register their own chain
            // (exception -> logging -> performance -> validation). Registered after them, a denial would run
            // inside the module's exception boundary instead of before it. Pinned by AuthorizationBehaviorOrderingTests.
            services.AddStommeAuthorization<PermissionRowAccessGuard, StommeCurrentUser, FeatureEvaluatorBridge>();

            // The shared per-site secret store (BL-78 CORRECTION / D101 - one seam for notifications, MCP, BL-81, BL-96):
            // private Site setting rows whose values are protected through ISecretProtector (ASP.NET Data Protection).
            // UNCONDITIONAL, like the bridge above. The key ring is kept where a redeploy does not lose it (BL-248,
            // DataProtectionKeyRing): a key ring lost made every secret stored before it Unreadable.
            Stomme.Infrastructure.Security.DataProtectionKeyRing.Configure(services, configuration, HostDataDirectory.Resolve(services));
            services.AddSecretProtection();
            services.AddScoped<ISiteSecretStore, SiteSecretStore>();

            // The site's visitor cookie, protected with the same Data Protection key ring (BL-185): the settings API's Visitor
            // door, the visitor API and the visit tracking all read it through this one service.
            services.TryAddSingleton<VisitorCookie>();

            // load the external assemblies into the app domain, install services
            services.AddStommeAssemblies();
            services.AddStommeDbContext();

            // integration message bus (opt-in via the Messaging section; no-op when absent).
            // Must follow AddStommeAssemblies so module handler assemblies are already loaded.
            services.AddStommeMessaging(configuration);

            // D105 (BL-2): the offline licence ring. Registers NOTHING unless Deployment:Mode=OnPrem (absent/Hosted leave
            // the descriptor list byte-identical, so FeatureManagement's entitlement leg stays permissive), and throws on
            // any other value. The "exactly one provider, and it is ours" rule is NOT checked here — module startups ran
            // in the scan above and could still be followed by later registrations — but post-build, first thing in
            // UseStommeAsync (EntitlementStartupValidation), before any hosted service starts.
            services.AddStommeLicensing(configuration);

            services.AddAntiforgery(options =>
            {
                options.HeaderName = Constants.AntiForgeryTokenHeaderName;
                options.Cookie.Name = Constants.AntiForgeryTokenCookieName;
                options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.HttpOnly = true;
            });

            services.AddIdentityCore<IdentityUser>(options => { })
                .AddEntityFrameworkStores<TenantDBContext>()
                .AddSignInManager()
                .AddDefaultTokenProviders()
                .AddClaimsPrincipalFactory<ClaimsPrincipalFactory<IdentityUser>>(); // role claims

            // D54b: make IUserStore<IdentityUser> a resolve-time choice between EF's UserStore (bound to
            // TenantDBContext above) and the hand-written RavenIdentityUserStore, keyed on the configured
            // database type — the same mechanism SelectFrameworkStoreProviderByConfig uses for the framework
            // store. A relational install resolves EF's store exactly as before; a RavenDB install resolves the
            // document store, so the install's host-account step (UserManager<IdentityUser>) no longer forces
            // TenantDBContext (which cannot be constructed on Raven). One swap covers all six identity facets:
            // UserManager casts the single IUserStore instance to each (password/email/security-stamp/logins/
            // passkeys), and both stores implement them all.
            services.SelectIdentityUserStoreProviderByConfig();

            services.ConfigureStommeIdentityOptions(configuration);

            services.AddCascadingAuthenticationState();
            services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
            services.AddAuthorization();

            // Capture the AuthenticationBuilder here — AddTwoFactorUserIdCookie() returns an OptionsBuilder,
            // which would break the chain for the D65 external-scheme registration below.
            var authenticationBuilder = services.AddAuthentication(options =>
            {
                options.DefaultScheme = Constants.AuthenticationScheme;
            })
            .AddCookie(Constants.AuthenticationScheme)
            .AddOpenIdConnect(AuthenticationProviderTypes.OpenIDConnect, options => { })
            .AddOAuth(AuthenticationProviderTypes.OAuth2, options => { });

            authenticationBuilder.AddTwoFactorUserIdCookie();

            // D65: installation-wide, config-gated SAML2 + Windows/Negotiate consumer schemes. OFF by default,
            // so a stock install registers neither and DefaultScheme stays the Identity cookie above. Both are
            // challenged ONLY explicitly by their login pages (never a default/global challenge — that would
            // force Windows auth on every request and break the D30 render guard). A successful assertion/
            // handshake routes through the same ValidateUser link-or-create bridge the OIDC consumer uses.
            authenticationBuilder.AddStommeExternalSchemes(configuration);

            services.ConfigureStommeCookieOptions();
            services.ConfigureStommeAuthenticationOptions(configuration);

            // D61 spike: per-tenant OpenIddict authority, co-hosted in this pipeline. No-op unless
            // OpenIddict:Enabled=true, so a stock relational/Raven install is unaffected.
            services.AddStommeOpenIddict(configuration, environment);

            services.AddStommeSiteOptions()
                .WithSiteIdentity()
                .WithSiteAuthentication();

            services.AddCors(options =>
            {
                options.AddPolicy(Constants.MauiCorsPolicy,
                    policy =>
                    {
                        // allow .NET MAUI client cross origin calls
                        policy.WithOrigins("https://0.0.0.1", "http://0.0.0.1", "app://0.0.0.1")
                            .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
                    });
            });

            services.AddCaching(configuration);
            services.AddOutputCache();

            services.AddMvc(options =>
            {
                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
                // D99 point 7: a [Requires]-denied mediator request escaping a controller is a 403 ProblemDetails, not a 500 (F5).
                options.Filters.Add<AccessDeniedExceptionFilter>();
                // BL-251: a validation failure or a stale write escaping an action is a 400 / 409, not a 500.
                options.Filters.Add<RequestRefusalExceptionFilter>();
            })
            // BL-251: an enum may be sent by name as well as by number; responses keep the numbers.
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new Stomme.Infrastructure.EnumNameOrNumberConverterFactory()))
            .AddStommeApplicationParts() // register any Controllers from custom modules
            .ConfigureStommeMvc(); // any additional configuration from IStartup classes

            services.AddRazorPages();

            services.AddRazorComponents()
               .AddInteractiveServerComponents(options =>
               {
                   if (environment.IsDevelopment())
                   {
                       options.DetailedErrors = true;
                   }
               }).AddHubOptions(options =>
               {
                   options.MaximumReceiveMessageSize = null; // no limit (for large amounts of data ie. textarea components)
               })
               .AddInteractiveWebAssemblyComponents();

            services.TryAddSwagger(configuration);

            // opt-in transports (gRPC-Web + SignalR) — no-op unless enabled in the Transport config section (D10)
            services.AddStommeTransport(configuration);

            return services;
        }

        public static IServiceCollection AddStommeServerScopedServices(this IServiceCollection services)
        {
            services.AddScoped<Stomme.Shared.SiteState>();
            services.AddScoped<IInstallationService, InstallationService>();
            services.AddScoped<IModuleDefinitionService, ModuleDefinitionService>();
            services.AddScoped<IThemeService, Stomme.Services.ThemeService>();
            services.AddScoped<IAliasService, AliasService>();
            services.AddScoped<ITenantService, TenantService>();
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
            services.AddScoped<ISearchResultsService, SearchResultsService>();
            services.AddScoped<ISearchService, SearchService>();
            services.AddScoped<ISearchProvider, DatabaseSearchProvider>();
            services.AddScoped<IImageService, ImageService>();
            services.AddScoped<ICookieConsentService, ServerCookieConsentService>();
            services.AddScoped<ITimeZoneService, TimeZoneService>();
            services.AddScoped<IMigrationHistoryService, MigrationHistoryService>();
            services.AddScoped<ISiteGroupService, SiteGroupService>();
            services.AddScoped<ISiteGroupMemberService, SiteGroupMemberService>();
            services.AddScoped<ISiteTaskService, SiteTaskService>();
            services.AddScoped<IOpenIddictService, Stomme.Services.OpenIddictService>();
            services.AddScoped<IFeatureService, Stomme.Services.FeatureService>();
            services.AddScoped<IDeadLetterService, Stomme.Services.DeadLetterService>();

            // providers
            services.AddScoped<ITextEditor, Stomme.Modules.Controls.QuillJSTextEditor>();
            services.AddScoped<ITextEditor, Stomme.Modules.Controls.TextAreaTextEditor>();

            services.AddFluentUIComponents();
            services.AddScoped<Stomme.Services.ThemeState>();

            return services;
        }
        public static IServiceCollection AddStommeSingletonServices(this IServiceCollection services)
        {
            services.AddSingleton<IInstallationManager, InstallationManager>();
            services.AddSingleton<ISyncManager, SyncManager>();
            services.AddSingleton<IDatabaseManager, DatabaseManager>();
            services.AddSingleton<IConfigManager, ConfigManager>();
            // Singleton: it IS the hand-off between every request thread that logs and the single
            // LogDrainService that writes (D34). LogDrainService is registered by the dynamic
            // IHostedService scan below, like every other hosted service — do not add it explicitly here.
            services.AddSingleton<LogQueue>();
            // What the hosts are told of the logs, held back when it repeats - one per node, as the drain is.
            services.AddSingleton<LogNotificationThrottle>();
            services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
            services.AddSingleton<AutoValidateAntiforgeryTokenFilter>();
            services.AddSingleton<IAuthorizationPolicyProvider, AuthorizationPolicyProvider>();
            return services;
        }

        public static IServiceCollection AddHttpClients(this IServiceCollection services)
        {
            // BL-234: the client's services call the site's own API from the server too; those calls go to the host's own
            // listener, never out through the edge and back (SelfCallHandler).
            services.AddSingleton<SelfCallAddress>();
            if (!services.Any(x => x.ServiceType == typeof(HttpClient)))
            {
                services.AddScoped(provider =>
                {
                    var client = new HttpClient(new SelfCallHandler(provider.GetRequiredService<SelfCallAddress>(), provider.GetRequiredService<IHttpContextAccessor>())
                    {
                        InnerHandler = new HttpClientHandler { UseCookies = false },
                    });
                    var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
                    if (httpContextAccessor.HttpContext != null)
                    {
                        client.BaseAddress = new Uri(httpContextAccessor.HttpContext.Request.Scheme + "://" + httpContextAccessor.HttpContext.Request.Host);
                        // set the cookies to allow HttpClient API calls to be authenticated
                        foreach (var cookie in httpContextAccessor.HttpContext.Request.Cookies)
                        {
                            client.DefaultRequestHeaders.Add("Cookie", cookie.Key + "=" + WebUtility.UrlEncode(cookie.Value));
                        }
                    }

                    return client;
                });
            }

            // register a named IHttpClientFactory
            services.AddHttpClient("stomme", (provider, client) =>
            {
                var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
                if (httpContextAccessor.HttpContext != null)
                {
                    client.BaseAddress = new Uri(httpContextAccessor.HttpContext.Request.Scheme + "://" + httpContextAccessor.HttpContext.Request.Host);
                    // set the cookies to allow HttpClient API calls to be authenticated
                    foreach (var cookie in httpContextAccessor.HttpContext.Request.Cookies)
                    {
                        client.DefaultRequestHeaders.Add("Cookie", cookie.Key + "=" + WebUtility.UrlEncode(cookie.Value));
                    }
                }
            }).AddHttpMessageHandler(provider => new SelfCallHandler(provider.GetRequiredService<SelfCallAddress>(), provider.GetRequiredService<IHttpContextAccessor>()));

            // IHttpClientFactory for calling remote services via RemoteServiceBase (not named = default)
            services.AddHttpClient();

            return services;
        }

        public static IServiceCollection AddStommeTransientServices(this IServiceCollection services)
        {
            // services
            services.AddTransient<ISiteService, ServerSiteService>();
            services.AddTransient<ILocalizationCookieService, ServerLocalizationCookieService>();
            services.AddTransient<IOutputCacheService, ServerOutputCacheService>();

            // repositories
            services.AddTransient<IModuleDefinitionRepository, ModuleDefinitionRepository>();
            services.AddTransient<IThemeRepository, ThemeRepository>();
            services.AddTransient<IAliasRepository, AliasRepository>();
            services.AddTransient<ITenantRepository, TenantRepository>();
            services.AddTransient<ISiteRepository, SiteRepository>();
            services.AddTransient<IPageRepository, PageRepository>();
            services.AddTransient<IModuleRepository, ModuleRepository>();
            services.AddTransient<IPageModuleRepository, PageModuleRepository>();
            services.AddTransient<IUserRepository, UserRepository>();
            services.AddTransient<IProfileRepository, ProfileRepository>();
            services.AddTransient<IRoleRepository, RoleRepository>();
            services.AddTransient<IUserRoleRepository, UserRoleRepository>();
            services.AddTransient<IPermissionRepository, PermissionRepository>();
            services.AddTransient<ISettingRepository, SettingRepository>();
            services.AddTransient<ILogRepository, LogRepository>();
            services.AddTransient<IJobRepository, JobRepository>();
            services.AddTransient<IJobLogRepository, JobLogRepository>();
            services.AddTransient<INotificationRepository, NotificationRepository>();
            services.AddTransient<IFolderRepository, FolderRepository>();

            // BL-142: take the Everyone View grant off user folders created before BL-115 closed the seed. This
            // is the FRAMEWORK's first ISiteContentReconciler, so the shared runner now has work on a stock
            // install - it walks each site once per boot, reads that site's user folders, and writes only where
            // the exposing row is still there.
            services.AddScoped<ISiteContentReconciler, UserFolderPrivacyReconciler>();

            // BL-103: a declared permission key reaches a site only when something reads that site's module
            // definitions, and the bridge DENIES a key with no rows - to administrators too. This asks each site
            // for them once on the boot path, which performs the reconciliation that was already there.
            services.AddScoped<ISiteContentReconciler, DeclaredPermissionKeyReconciler>();

            // BL-203: a deployment that lost its content folder gets the SEEDED logo back on the next boot.
            services.AddScoped<ISiteContentReconciler, SeededLogoReconciler>();
            services.AddTransient<IFileRepository, FileRepository>();
            services.AddTransient<ISiteTemplateRepository, SiteTemplateRepository>();
            services.AddTransient<ISqlRepository, SqlRepository>();
            services.AddTransient<ILanguageRepository, LanguageRepository>();
            services.AddTransient<IVisitorRepository, VisitorRepository>();
            services.AddTransient<IUrlMappingRepository, UrlMappingRepository>();
            services.AddTransient<ISearchContentRepository, SearchContentRepository>();
            services.AddTransient<IMigrationHistoryRepository, MigrationHistoryRepository>();
            services.AddTransient<ISiteGroupRepository, SiteGroupRepository>();
            services.AddTransient<ISiteGroupMemberRepository, SiteGroupMemberRepository>();
            services.AddTransient<ISiteTaskRepository, SiteTaskRepository>();

            // managers
            services.AddTransient<IDBContextDependencies, DBContextDependencies>();
            services.AddTransient<ITenantManager, TenantManager>();

            // Scoped, alongside SiteState: the tenants already resolved in this scope, keyed by TenantId, so
            // DbContext.OnConfiguring can route without a store read (D37). It MUST be scoped rather than
            // transient — ITenantManager and the contexts are transient, so the scope is the only thing a
            // SetTenant on one instance and an OnConfiguring on another share. Holds pure state and no
            // dependency, so it captures nothing.
            services.AddScoped<Stomme.Infrastructure.Tenancy.ITenantStoreBindings, Stomme.Infrastructure.Tenancy.TenantStoreBindings>();
            services.AddTransient<IAliasAccessor, AliasAccessor>();
            services.AddTransient<IUserPermissions, UserPermissions>();
            services.AddTransient<ITenantResolver, TenantResolver>();
            services.AddTransient<IJwtManager, JwtManager>();
            services.AddTransient<ILogManager, LogManager>();
            services.AddTransient<IUpgradeManager, UpgradeManager>();
            services.AddTransient<IUserManager, UserManager>();
            services.AddTransient<ILocalizationManager, LocalizationManager>();
            services.AddTransient<ITokenReplace, TokenReplace>();

            // obsolete
            services.AddTransient<ITenantResolver, TenantResolver>(); // replaced by ITenantManager

            return services;
        }

        /// <summary>
        /// Fronts the persistence seam's tenancy ports onto Stomme's real tenancy model — Alias -> Site ->
        /// Tenant, database-per-tenant — so a building block written against the seam (the flag store today;
        /// the framework store, identity, and the bus next) sees the tenant the request actually resolved to,
        /// instead of the single-tenant fallback. See D16 and src/Stomme.Server/README.md (the tenancy adapter).
        /// </summary>
        public static IServiceCollection AddStommeTenancy(this IServiceCollection services)
        {
            // Scoped: the tenant is a property of the request/scope, and these read ITenantManager ->
            // SiteState, which is itself scoped. Never resolve them from the root provider.
            services.AddScoped<ITenantContext, StommeTenantContext>();
            services.AddScoped<ITenantConnectionResolver, StommeTenantConnectionResolver>();
            services.AddScoped<ITenantDirectory, StommeTenantDirectory>();

            // Singleton: holds only an AsyncLocal, no scoped dependency. Its callers are singletons (a hosted
            // service, a bus pipeline step), so it must be resolvable from the root provider.
            services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();

            return services;
        }

        public static IServiceCollection AddStommeAssemblies(this IServiceCollection services)
        {
            LoadAssemblies();
            LoadSatelliteAssemblies();
            services.AddStommeServices();

            return services;
        }

        private static void LoadAssemblies()
        {
            var assemblyPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
            if (assemblyPath == null) return;

            AssemblyLoadContext.Default.Resolving += ResolveDependencies;

            var assembliesFolder = new DirectoryInfo(assemblyPath);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // iterate through Stomme assemblies in /bin ( filter is narrow to optimize loading process )
            foreach (var dll in assembliesFolder.EnumerateFiles($"*.dll", SearchOption.TopDirectoryOnly).Where(f => f.IsStommeAssembly()))
            {
                AssemblyName assemblyName;
                try
                {
                    assemblyName = AssemblyName.GetAssemblyName(dll.FullName);
                }
                catch
                {
                    Debug.WriteLine($"Stomme Error: Cannot Get Assembly Name For {dll.Name}");
                    continue;
                }

                if (!assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(assemblyName, a.GetName())))
                {
                    AssemblyLoadContext.Default.LoadStommeAssembly(dll, assemblyName);
                }
            }
        }

        private static void LoadSatelliteAssemblies()
        {
            AssemblyLoadContext.Default.Resolving += ResolveDependencies;

            var installedCultures = LocalizationManager.GetSatelliteAssemblyCultures();
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), $"*{Constants.SatelliteAssemblyExtension}", SearchOption.AllDirectories))
            {
                var code = Path.GetFileName(Path.GetDirectoryName(file));
                if (installedCultures.Contains(code))
                {
                    try
                    {
                        Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(System.IO.File.ReadAllBytes(file)));
                        Debug.WriteLine($"Stomme Info: Loaded Satellite Assembly {file}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Stomme Error: Unable To Load Satellite Assembly {file} - {ex}");
                    }
                }
                else
                {
                    Debug.WriteLine($"Stomme Error: Culture Not Supported For Satellite Assembly {file}");
                }
            }
        }

        private static IServiceCollection AddStommeServices(this IServiceCollection services)
        {
            if (services is null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            var hostedServiceType = typeof(IHostedService);

            // M2 (ruling C33): module.<id>.enabled=false switches a module assembly's hosted services off on this host, so one
            // image can carry a module that runs on one host only. Read straight from configuration (the FeatureManagement
            // section): this runs before any container exists. Silent configuration leaves everything on.
            var configuration = HostConfiguration.Resolve(services);
            var switches = configuration is null ? null : Stomme.FeatureManagement.Infrastructure.FeatureConfigurationSources.FromConfiguration(configuration);

            var assemblies = AppDomain.CurrentDomain.GetStommeAssemblies();
            foreach (var assembly in assemblies)
            {
                // Framework building-block libraries opt out of the module convention scan: they wire their
                // own DI explicitly via an Add*() extension the host calls, usually behind a config gate
                // (Messaging:Enabled, Transport:*:Enabled). Convention-registering their hosted services
                // would activate them regardless of that gate while their dependencies stay unregistered,
                // which breaks container construction. See docs/decisions.md D13.
                if (assembly.IsStommeFrameworkLibrary())
                {
                    continue;
                }

                // dynamically register module scoped services (ie. client service classes)
                var implementationTypes = assembly.GetInterfaces<IService>();
                foreach (var implementationType in implementationTypes)
                {
                    if (implementationType.AssemblyQualifiedName != null)
                    {
                        var serviceType = Type.GetType(implementationType.AssemblyQualifiedName.Replace(implementationType.Name, $"I{implementationType.Name}"));
                        services.AddScoped(serviceType ?? implementationType, implementationType);
                    }
                }

                // dynamically register module transient services (ie. server DBContext, repository classes)
                implementationTypes = assembly.GetInterfaces<ITransientService>();
                foreach (var implementationType in implementationTypes)
                {
                    if (implementationType.AssemblyQualifiedName != null)
                    {
                        var serviceType = Type.GetType(implementationType.AssemblyQualifiedName.Replace(implementationType.Name, $"I{implementationType.Name}"));
                        services.AddTransient(serviceType ?? implementationType, implementationType);
                    }
                }

                // dynamically register hosted services.
                // TryAddEnumerable de-duplicates on the IMPLEMENTATION type, which is what a hosted-service
                // registration is keyed by: AddHostedService<T>() registers ServiceType == IHostedService, so
                // the previous "ServiceType == concrete type" guard could never match and never prevented the
                // double registration it was written for.
                var serviceTypes = assembly.GetTypes(hostedServiceType);
                var runsSomething = serviceTypes.Any() || assembly.GetTypes(typeof(Stomme.Transport.IModuleEndpointContributor)).Any()
                    || assembly.GetTypes(typeof(IModuleHttpEndpointContributor)).Any();
                if (switches is not null && runsSomething && ModuleFeatureFlags.IsSwitchedOff(switches, assembly))
                {
                    services.AddSingleton(new SwitchedOffModuleAssembly(assembly.GetName().Name, ModuleFeatureFlags.SwitchIds(assembly)));
                    serviceTypes = Enumerable.Empty<Type>();
                }

                foreach (var serviceType in serviceTypes)
                {
                    services.TryAddEnumerable(ServiceDescriptor.Singleton(hostedServiceType, serviceType));
                }

                // dynamically register server startup services
                assembly.GetInstances<IServerStartup>()
                    .ToList()
                    .ForEach(x => x.ConfigureServices(services));

                // dynamically register client startup services (these services will only be used when running on Blazor Server)
                assembly.GetInstances<IClientStartup>()
                    .ToList()
                    .ForEach(x => x.ConfigureServices(services));
            }
            return services;
        }

        private static Assembly ResolveDependencies(AssemblyLoadContext context, AssemblyName name)
        {
            var assemblyPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) + Path.DirectorySeparatorChar + name.Name + ".dll";
            if (System.IO.File.Exists(assemblyPath))
            {
                return context.LoadFromStream(new MemoryStream(System.IO.File.ReadAllBytes(assemblyPath)));
            }
            else
            {
                return null;
            }
        }

        public static IServiceCollection AddStommeDbContext(this IServiceCollection services)
        {
            services.AddDbContext<MasterDBContext>(options => { }, ServiceLifetime.Transient);
            services.AddDbContext<TenantDBContext>(options => { }, ServiceLifetime.Transient);
            services.AddDbContextFactory<TenantDBContext>(opt => { }, ServiceLifetime.Transient);
            // The master half of the Setting cluster's store resolves its own context per instance rather
            // than taking the transient MasterDBContext above, mirroring the tenant factory on the line
            // above it (D23). Closed-generic, so it cannot collide with the DbContext registration
            // AddFeatureManagement owns.
            services.AddDbContextFactory<MasterDBContext>(opt => { }, ServiceLifetime.Transient);

            // BL-81 (D106 item 3): the framework's own tables in the installation snapshot - beside the contexts they read.
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Backup.IBackupParticipant, Stomme.Infrastructure.Backup.Participants.FrameworkDatabasesBackupParticipant>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Backup.IBackupParticipant, Stomme.Infrastructure.Backup.Participants.TenantContentFilesBackupParticipant>());
            return services;
        }

        // Builds the RavenDB document store the framework-store provider addresses, from the persisted
        // ConnectionStrings:DefaultConnection (D54). Invoked LAZILY on the first resolve of an IDocumentStore,
        // which only happens on a RavenDB install; a relational install never calls this and never connects to
        // RavenDB. The connection string is a RavenDB descriptor of the form
        // "Url=http://host:port;Database=name" (Urls= with a comma-separated list is accepted for a cluster).
        private static IDocumentStore BuildRavenFrameworkDocumentStore(IServiceProvider provider)
        {
            var config = provider.GetRequiredService<IConfigManager>();
            var (urls, database) = ParseRavenConnectionString(config.GetConnectionString(SettingKeys.ConnectionStringKey));

            var store = (DocumentStore)new DocumentStore
            {
                Urls = urls,
                Database = database
            }.InitializeStommeStore();

            // The document provider has no change tracker to decide "is this an insert?", so audit stamps come
            // from a store-level listener (D26). The context is the REQUEST's (BL-175, found by the erasure drive
            // 2026-09-27): until then the listener carried SystemAuditContext(string.Empty), so a RavenDB install
            // stamped every row with an empty author for its whole life while the relational path stamped the
            // signed-in name - measured, a page POSTed by a signed-in administrator came back CreatedBy "". With no
            // request (install, jobs) the stamp is the empty string, exactly what DbContextUtils writes then, and
            // the relational path's AuditStampSuppression is honoured here too.
            store.StampAudit(new Stomme.Infrastructure.RequestAuditContext(provider.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()));
            return store;
        }

        private static (string[] Urls, string Database) ParseRavenConnectionString(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "A RavenDB framework store was resolved but ConnectionStrings:DefaultConnection is empty. It " +
                    "must be a RavenDB descriptor, e.g. \"Url=http://127.0.0.1:7000;Database=Stomme\".");
            }

            string database = null;
            var urls = new List<string>();
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = part.Substring(0, separator).Trim();
                var value = part.Substring(separator + 1).Trim();
                if (key.Equals("Url", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Urls", StringComparison.OrdinalIgnoreCase))
                {
                    urls.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    database = value;
                }
            }

            if (urls.Count == 0 || string.IsNullOrEmpty(database))
            {
                throw new InvalidOperationException(
                    $"The RavenDB connection string \"{connectionString}\" must specify both a Url and a Database, " +
                    "e.g. \"Url=http://127.0.0.1:7000;Database=Stomme\".");
            }

            return (urls.ToArray(), database);
        }

        public static IServiceCollection ConfigureStommeIdentityOptions(this IServiceCollection services, IConfigurationRoot Configuration)
        {
            // default settings
            services.Configure<IdentityOptions>(options =>
            {
                // Password settings
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 6;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequiredUniqueChars = 1;

                // Lockout settings
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.AllowedForNewUsers = false;

                // SignIn settings
                options.SignIn.RequireConfirmedEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
                options.SignIn.RequireConfirmedPhoneNumber = false;

                // User settings
                options.User.RequireUniqueEmail = false;
                options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
            });

            // overrides defined in appsettings
            services.Configure<IdentityOptions>(Configuration);

            return services;
        }

        public static IServiceCollection ConfigureStommeCookieOptions(this IServiceCollection services)
        {
            // note that ConfigureApplicationCookie internally uses an ApplicationScheme of "Identity.Application"
            services.ConfigureApplicationCookie(options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/login"; // overrides .NET Identity default of /Account/Login
                options.Events.OnRedirectToLogin = context =>
                {
                    // A bare 403 is the right answer for the SPA: its callers are XHR, and an HTML login page
                    // in a fetch() response is useless. But BL-71 measured what that costs the ONE challenge
                    // that IS a browser navigation — the D64 interactive OIDC hop. An external client sending
                    // a user to /connect/authorize got 403 + the SPA shell and rendered a blank window, so no
                    // external relying party could ever complete an interactive login.
                    if (IsInteractiveAuthorizeRequest(context.Request))
                    {
                        context.Response.Redirect(BuildLoginRedirect(context.HttpContext));
                        return Task.CompletedTask;
                    }

                    // The MCP endpoint is the second carve-out, and for the same kind of reason as the first: 403 is
                    // the wrong answer to "you have not authenticated". HTTP says 401 means authenticate and 403 means
                    // you may not, and an MCP client cannot tell "sign in" from "this is not for you" out of a 403 -
                    // it is the status the protocol's own authorization flow keys on. No WWW-Authenticate header is
                    // invented here: Wave 1 admits the Identity cookie only, and advertising Bearer with an RFC 9728
                    // resource_metadata URL that does not exist would send every client to a 404. That header belongs
                    // with the OAuth story, not with this.
                    if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<Stomme.Modules.Mcp.Server.McpEndpointMarker>() is not null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    // Deliberately NOT carved out. AccessDenied fires only for an AUTHENTICATED principal that
                    // failed authorization, and the authorize endpoint challenges only when NOT authenticated
                    // (once authenticated it either signs in or forbids on the OpenIddict scheme, not this
                    // one), so this event cannot fire for /connect/authorize.
                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToLogout = context =>
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = PrincipalValidator.ValidateAsync;
                // BL-119: every cookie issued opens a server-side session, and a sign-out ends it.
                options.Events.OnSigningIn = SessionCookieEvents.SigningInAsync;
                options.Events.OnSigningOut = SessionCookieEvents.SigningOutAsync;
            });

            return services;
        }

        /// <summary>
        /// BL-71 — is this challenge the interactive OIDC authorization hop? The authority's authorization
        /// endpoint is mapped at the fixed root path (<c>MapStommeOpenIddict</c>), independent of the tenant's
        /// alias path, so an exact segment match is the whole test.
        /// </summary>
        private static bool IsInteractiveAuthorizeRequest(HttpRequest request) =>
            request.Path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Builds the tenant's login URL carrying the ORIGINAL request as <c>returnurl</c> — the same shape
        /// <c>SiteRouter</c> uses when a page needs authentication, so the Login module, the
        /// <c>/pages/login</c> postback and its <c>LocalRedirect</c> all handle it unchanged.
        /// </summary>
        /// <remarks>
        /// The alias PATH matters: on a site installed at <c>host/folder</c> the login page is
        /// <c>/folder/login</c> while the authority endpoint stays at the root. Built by string rather than
        /// <c>UriBuilder</c>, which re-escapes an already-encoded query and would corrupt the returnurl.
        /// </remarks>
        private static string BuildLoginRedirect(HttpContext context)
        {
            var aliasPath = context.GetAlias()?.Path?.Trim('/');
            var loginPath = string.IsNullOrEmpty(aliasPath) ? "/login" : $"/{aliasPath}/login";
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;

            return $"{loginPath}?returnurl={WebUtility.UrlEncode(returnUrl)}";
        }

        public static IServiceCollection ConfigureStommeAuthenticationOptions(this IServiceCollection services, IConfigurationRoot Configuration)
        {
            // prevent remapping of claims
            JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

            // settings defined in appsettings
            services.Configure<OAuthOptions>(Configuration);
            services.Configure<OpenIdConnectOptions>(Configuration);

            return services;
        }

        public static StommeSiteOptionsBuilder AddStommeSiteOptions(this IServiceCollection services)
        {
            return new StommeSiteOptionsBuilder(services);
        }

        /// <summary>
        /// The cache's default entry options from the <c>Caching</c> section (managed in System Info), each value defaulting on
        /// its own: <c>Duration</c> 10 minutes when absent or not positive, <c>FailSafe</c> on when absent. BL-251: a PARTIAL
        /// section - <c>{"Caching": {"Distributed": true}}</c> - read the missing Duration as 0 and FailSafe as off, so
        /// nothing was cached at all.
        /// </summary>
        internal static FusionCacheEntryOptions CacheEntryDefaults(IConfigurationSection cacheSettings)
        {
            var minutes = cacheSettings.GetValue<int?>("Duration") is int duration && duration > 0 ? duration : 10;
            var options = new FusionCacheEntryOptions
            {
                ReThrowDistributedCacheExceptions = false, // suppress serialization exceptions
                MemoryCacheDuration = TimeSpan.FromMinutes(minutes),
                IsFailSafeEnabled = cacheSettings.GetValue<bool?>("FailSafe") ?? true,
                DistributedCacheDuration = TimeSpan.FromMinutes(minutes * 3),
            };
            if (options.IsFailSafeEnabled)
            {
                options.FailSafeMaxDuration = TimeSpan.FromMinutes(minutes * 3);
                options.FailSafeThrottleDuration = TimeSpan.FromMinutes(Math.Max(1, minutes / 5));
            }

            return options;
        }

        public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
        {
            var fusionCacheOptions = new FusionCacheOptions
            {
                // use installationid as a unique prefix so that a single distributed cache service can be shared by multiple Stomme installations
                CacheKeyPrefix = configuration.GetSection("InstallationId").Value + ":",
                BackplaneChannelPrefix = configuration.GetSection("InstallationId").Value + ":"
            };

            var cacheSettings = configuration.GetSection("Caching");
            var defaultCacheEntryOptions = CacheEntryDefaults(cacheSettings);

            if (!cacheSettings.GetValue<bool>("Distributed") && !cacheSettings.GetValue<bool>("ScaleOut"))
            {
                // use memory cache (L1 only)
                services.AddFusionCache()
                    .WithOptions(fusionCacheOptions)
                    .WithDefaultEntryOptions(defaultCacheEntryOptions);
            }
            else
            {
                if (cacheSettings.GetValue<bool>("Distributed") && cacheSettings.GetValue<bool>("ScaleOut"))
                {
                    services.AddStackExchangeRedisCache(options => {
                        options.Configuration = configuration.GetConnectionString(SettingKeys.DistributedCacheKey);
                    });

                    // use memory cache (L1) with distributed cache (L2) and backplane for synchronization across instances
                    services.AddFusionCache()
                        .WithOptions(fusionCacheOptions)
                        .WithDefaultEntryOptions(defaultCacheEntryOptions)
                        .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                        .WithRegisteredDistributedCache()
                        .WithBackplane(new RedisBackplane(new RedisBackplaneOptions
                        {
                            Configuration = configuration.GetConnectionString(SettingKeys.DistributedCacheKey)
                        }));
                }
                else
                {
                    services.AddStackExchangeRedisCache(options => {
                        options.Configuration = configuration.GetConnectionString(SettingKeys.DistributedCacheKey);
                    });

                    if (cacheSettings.GetValue<bool>("ScaleOut"))
                    {
                        // use memory cache (L1) with backplane for synchronization across instances
                        services.AddFusionCache()
                            .WithOptions(fusionCacheOptions)
                            .WithDefaultEntryOptions(defaultCacheEntryOptions)
                            .WithBackplane(new RedisBackplane(new RedisBackplaneOptions
                            {
                                Configuration = configuration.GetConnectionString(SettingKeys.DistributedCacheKey)
                            }));
                    }
                    else
                    {
                        // use memory cache (L1) with distributed cache (L2)
                        services.AddFusionCache()
                            .WithOptions(fusionCacheOptions)
                            .WithDefaultEntryOptions(defaultCacheEntryOptions)
                            .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                            .WithRegisteredDistributedCache();
                    }
                }
            }

            services.AddSingleton<ICacheManager, CacheManager>();
            services.AddSingleton<IDistributedLockManager, DistributedLockManager>();

            // BL-154: the ONE registered IDistributedLock, choosing its primitive from the install's store at resolve
            // time (PostgreSQL advisory, SQL Server app lock, RavenDB compare-exchange, or the explicit single-node
            // in-process lock). TryAdd, so a host or a test that registered its own keeps it.
            services.TryAddSingleton<Stomme.Persistence.Abstractions.IDistributedLock, Stomme.Infrastructure.Locks.ConfiguredDistributedLock>();

            return services;
        }

        public static IServiceCollection TryAddSwagger(this IServiceCollection services, IConfigurationRoot configuration)
        {
            if (configuration.GetSection("UseSwagger").Value == "true")
            {
                services.AddOpenApi();
            }

            return services;
        }
    }
}
