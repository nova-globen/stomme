using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Controllers;
using Stomme.Notifications;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications.Persistence;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The Notifications module over the authorization bridge: the five SPLIT keys, REFLECTIVELY
    /// (declaration, per-key roles, every request's key, every controller action's key, no anonymous bypass) and DRIVEN over real
    /// HTTP on an installed host (the <see cref="LiftedRequiresGateTests"/> recipe).
    /// </summary>
    /// <remarks>
    /// The reflective half is necessary but proves nothing by itself: a rename once shipped broken with every compile-time
    /// check green. The driven half answers the only questions that matter - does a registered user read ONLY their own data, is
    /// the send log and the preview refused them, is an anonymous caller refused, and does a revocation (an Administrators DENY
    /// row) take effect on the very next request. SQLite here; the drive repeats on PostgreSQL and
    /// RavenDB (<see cref="Stores"/> is the seam). Fail-not-skip is inherited from <see cref="IGateStore"/>.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class NotificationPermissionDeclarationTests
    {
        public const string ModuleInfoTypeName = "Stomme.Modules.Notifications.ModuleInfo, Stomme.Client";

        private readonly ITestOutputHelper _output;

        public NotificationPermissionDeclarationTests(ITestOutputHelper output) => _output = output;

        /// <summary>Every user-facing request of the module and the ONE key it must carry. Preview is template authoring.</summary>
        public static readonly IReadOnlyDictionary<Type, string> ExpectedRequestKeys = new Dictionary<Type, string>
        {
            [typeof(ListMyNotificationsQuery)] = NotificationsPermissions.ViewOwn,
            [typeof(GetMyNotificationPreferencesQuery)] = NotificationsPermissions.ViewOwn,
            [typeof(SaveMyNotificationPreferencesCommand)] = NotificationsPermissions.ViewOwn,
            [typeof(ListNotificationRequestsQuery)] = NotificationsPermissions.ViewLog,
            [typeof(GetNotificationRequestQuery)] = NotificationsPermissions.ViewLog,
            [typeof(SendTestNotificationCommand)] = NotificationsPermissions.Send,
            [typeof(RetryNotificationRequestCommand)] = NotificationsPermissions.Send,
            [typeof(ConfigureChannelCommand)] = NotificationsPermissions.ManageChannels,
            [typeof(SetChannelCredentialCommand)] = NotificationsPermissions.ManageChannels,
            [typeof(EnableChannelCommand)] = NotificationsPermissions.ManageChannels,
            [typeof(DisableChannelCommand)] = NotificationsPermissions.ManageChannels,
            [typeof(DeleteChannelCommand)] = NotificationsPermissions.ManageChannels,
            [typeof(ListNotificationChannelsQuery)] = NotificationsPermissions.ManageChannels,
            [typeof(GetNotificationChannelQuery)] = NotificationsPermissions.ManageChannels,
            [typeof(SaveNotificationTemplateCommand)] = NotificationsPermissions.ManageTemplates,
            [typeof(DeleteNotificationTemplateCommand)] = NotificationsPermissions.ManageTemplates,
            [typeof(ListNotificationTemplatesQuery)] = NotificationsPermissions.ManageTemplates,
            [typeof(GetNotificationTemplateQuery)] = NotificationsPermissions.ManageTemplates,
            [typeof(PreviewNotificationTemplateQuery)] = NotificationsPermissions.ManageTemplates,
        };

        // ---- REFLECTIVE ------------------------------------------------------------------------------------------------

        internal static ModuleDefinition Definition()
        {
            var type = Type.GetType(ModuleInfoTypeName);
            Assert.True(type != null, $"'{ModuleInfoTypeName}' does not exist - the module declares nothing, so every key has no row and denies everyone.");
            return ((IModule)Activator.CreateInstance(type!)!).ModuleDefinition;
        }

        [Fact]
        public void The_module_declares_exactly_the_five_split_keys_View_Edit_first_with_per_key_roles()
        {
            var definition = Definition();
            _output.WriteLine($"PermissionNames = {definition.PermissionNames}");

            var entries = PermissionKeys.Entries(definition.PermissionNames);
            Assert.Equal(PermissionNames.View, entries[0]);
            Assert.Equal(PermissionNames.Edit, entries[1]);

            var keys = PermissionKeys.ParseKeys(definition.PermissionNames);
            Assert.Equal(5, keys.Count);
            Assert.Equal(NotificationsPermissions.All.OrderBy(key => key), keys.Select(key => key.Key).OrderBy(key => key));
            Assert.DoesNotContain(keys, key => key.Key == "notifications:view"); // the single original key is split, not kept

            Assert.Equal(new[] { RoleNames.Registered, RoleNames.Admin }, keys.Single(key => key.Key == NotificationsPermissions.ViewOwn).Roles);
            foreach (var adminOnly in new[] { NotificationsPermissions.ViewLog, NotificationsPermissions.Send, NotificationsPermissions.ManageChannels, NotificationsPermissions.ManageTemplates })
            {
                Assert.Equal(new[] { RoleNames.Admin }, keys.Single(key => key.Key == adminOnly).Roles);
            }

            // ...and the declaration is exactly what the per-key form produces - no hand-written string.
            Assert.Equal(PermissionKeys.Declare(NotificationsPermissions.Declarations), definition.PermissionNames);
        }

        [Fact]
        public void The_module_passes_the_framework_declaration_gate()
        {
            var definition = Definition();
            Assert.Empty(PermissionKeyDeclarationGateTests.ValidateDeclarations(new[] { (definition.Name, definition.PermissionNames) }));
        }

        [Fact]
        public void Every_user_facing_request_carries_exactly_its_one_split_key_and_nothing_else_in_the_module_carries_one()
        {
            var moduleTypes = typeof(NotificationWire).Assembly.GetTypes()
                .Where(type => type.Namespace != null && (type.Namespace.StartsWith("Stomme.Modules.Notifications", StringComparison.Ordinal)
                    || type.Namespace.StartsWith("Stomme.Notifications", StringComparison.Ordinal)))
                .ToList();

            var requestTypes = moduleTypes.Where(type => !type.IsAbstract && type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))).ToList();
            Assert.Equal(ExpectedRequestKeys.Keys.OrderBy(type => type.FullName), requestTypes.OrderBy(type => type.FullName));

            foreach (var (type, expected) in ExpectedRequestKeys)
            {
                var requires = type.GetCustomAttributes<RequiresAttribute>(inherit: true).ToList();
                Assert.True(requires.Count == 1, $"{type.Name} carries {requires.Count} [Requires].");
                Assert.Equal(expected, requires[0].Permission);
                Assert.True(string.IsNullOrEmpty(requires[0].Feature), $"{type.Name} carries a feature requirement.");
            }

            // Nothing on the dispatch/job path (dispatcher, processor, hint handler, channels) is gated - a job has no HttpContext.
            var gatedElsewhere = moduleTypes.Where(type => !requestTypes.Contains(type)
                && (type.GetCustomAttributes<RequiresAttribute>(inherit: true).Any()
                    || type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Any(method => method.GetCustomAttributes<RequiresAttribute>().Any())))
                .Select(type => type.FullName)
                .ToList();
            Assert.Empty(gatedElsewhere);

            // No request is an IStreamRequest / INotification (a [Requires] there is never enforced).
            Assert.DoesNotContain(requestTypes, type => typeof(INotification).IsAssignableFrom(type)
                || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>)));
        }

        [Fact]
        public void Every_controller_action_carries_its_key_and_nothing_is_reachable_anonymously()
        {
            var controller = typeof(NotificationsController);
            Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Empty(controller.GetCustomAttributes<RequiresPermissionKeyAttribute>(inherit: true)); // per ACTION, not class-level

            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
                .ToList();
            Assert.True(actions.Count >= 18, $"only {actions.Count} actions discovered");

            var byAction = new Dictionary<string, string>();
            foreach (var action in actions)
            {
                Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
                var keys = action.GetCustomAttributes<RequiresPermissionKeyAttribute>().ToList();
                Assert.True(keys.Count == 1, $"{action.Name} carries {keys.Count} [RequiresPermissionKey].");
                Assert.Contains(keys[0].Key, NotificationsPermissions.All);
                byAction[action.Name] = keys[0].Key;
            }

            Assert.Equal(NotificationsPermissions.ViewOwn, byAction["Mine"]);
            Assert.Equal(NotificationsPermissions.ViewOwn, byAction["GetPreferences"]);
            Assert.Equal(NotificationsPermissions.ViewOwn, byAction["SavePreferences"]);
            Assert.Equal(NotificationsPermissions.ViewLog, byAction["Log"]);
            Assert.Equal(NotificationsPermissions.ViewLog, byAction["LogEntry"]);
            Assert.Equal(NotificationsPermissions.Send, byAction["SendTest"]);
            Assert.Equal(NotificationsPermissions.Send, byAction["Retry"]);
            Assert.Equal(NotificationsPermissions.ManageTemplates, byAction["Preview"]);
            Assert.Equal(NotificationsPermissions.ManageTemplates, byAction["Templates"]);
            Assert.Equal(NotificationsPermissions.ManageChannels, byAction["SetChannelCredential"]);

            // The framework's own endpoint gate agrees (it is what fails the build for everyone else).
            Assert.Empty(PermissionKeyDeclarationGateTests.ValidateKeyGatedEndpoints(PermissionKeyDeclarationGateTests.DiscoverKeyGatedEndpoints(new[] { controller })));
        }

        [Fact]
        public void The_module_seeds_user_and_admin_pages_for_every_alias_and_declares_its_config_control()
        {
            var definition = Definition();

            Assert.NotEmpty(definition.PageTemplates);
            foreach (var template in definition.PageTemplates)
            {
                _output.WriteLine($"page '{template.Path}' alias '{template.AliasName}' modules {template.PageTemplateModules.Count}");
                Assert.Equal("*", template.AliasName); // CreatePagesAsync skips an EMPTY AliasName when a real alias is passed
                Assert.NotEmpty(template.PageTemplateModules);
            }

            var configType = Type.GetType(definition.ConfigSettingsType);
            Assert.True(configType != null, $"ConfigSettingsType '{definition.ConfigSettingsType}' does not resolve.");
            Assert.True(typeof(Stomme.Interfaces.ISettingsControl).IsAssignableFrom(configType), "the config control is not an ISettingsControl");

            // The controls the pages place exist in the Client assembly under the module's namespace.
            foreach (var control in new[] { "Index", "Preferences", "Templates", "Channels", "Log", "Settings" })
            {
                Assert.True(Type.GetType($"Stomme.Modules.Notifications.{control}, Stomme.Client") != null, $"control '{control}' is missing");
            }
        }

        [Fact]
        public void The_client_settings_control_writes_the_setting_names_the_server_reads()
        {
            var settings = Type.GetType("Stomme.Modules.Notifications.Settings, Stomme.Client")!;
            string Constant(string name) => (string)settings.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

            Assert.Equal(NotificationSiteSettingNames.ExternalRecipients, Constant("ExternalRecipientsSetting"));
            Assert.Equal(NotificationSiteSettingNames.RequestRetention, Constant("RequestRetentionSetting"));
            Assert.Equal(Stomme.Modules.Notifications.Infrastructure.Channels.WebhookSsrfGuard.AllowedHostsSettingName, Constant("WebhookAllowedHostsSetting"));
        }

        // ---- DRIVEN over real HTTP ---------------------------------------------------------------------------------------

        public static TheoryData<string> Stores() => new TheoryData<string>
        {
            SqliteGateStore.StoreName,
        };

        [Theory]
        [MemberData(nameof(Stores))]
        public Task The_split_keys_decide_over_http(string storeName) => DriveAsync(LiftedRequiresGateTests.ResolveStore(storeName), _output);

        /// <summary>The whole drive, store-parameterised so the drive runs the identical assertions on PostgreSQL and RavenDB.</summary>
        internal static async Task DriveAsync(IGateStore store, ITestOutputHelper output)
        {
            const string Host = LiftedRequiresGateTests.Host;
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, Host);
            var app = await GateDrive.InstallAsync(scratch, output);

            try
            {
                // ---- 1. THE DECLARATION, SEEDED: view-own to Registered Users + Administrators, view-log to Administrators only --
                var definition = await NotificationsDefinitionAsync(app, Host);
                output.WriteLine($"[{store.Name}] definition {definition.ModuleDefinitionId} declares '{definition.PermissionNames}'");
                output.WriteLine($"[{store.Name}] notifications rows = {GateDrive.Describe(definition.PermissionList.Where(row => row.EntityName == NotificationsPermissions.Resource))}");
                Assert.Contains(definition.PermissionList, row => IsKeyRow(row, "view-own", RoleNames.Registered, true));
                Assert.Contains(definition.PermissionList, row => IsKeyRow(row, "view-own", RoleNames.Admin, true));
                Assert.Contains(definition.PermissionList, row => IsKeyRow(row, "view-log", RoleNames.Admin, true));
                Assert.DoesNotContain(definition.PermissionList, row => row.EntityName == NotificationsPermissions.Resource && row.PermissionName == "view-log" && row.RoleName == RoleNames.Registered);

                // ---- 2. THE PRINCIPALS AND THEIR DATA ----------------------------------------------------------------------
                var plain = await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var plainRequest = await DispatchToAsync(app, Host, plain.UserId);
                var hostRequest = await DispatchToAsync(app, Host, hostUserId);
                await SaveHostPreferenceAsync(app, Host, hostUserId);
                var templateId = await GenericTemplateIdAsync(app, Host);
                output.WriteLine($"[{store.Name}] plain user {plain.UserId} request {plainRequest}; host {hostUserId} request {hostRequest}; template {templateId}");

                using var hostClient = GateDrive.NewClient(app, Host);
                using var plainClient = GateDrive.NewClient(app, Host);
                using var anonymousClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var previewBody = new NotificationTemplatePreviewInput(templateId, "en", new[] { new DispatchParameter("subject", "Hi"), new DispatchParameter("body", "<b>there</b>") });

                // ---- 3. A REGISTERED USER: ONLY their own notifications and preferences; 403 on the send log and the preview -
                var response = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Notifications/mine", output, store.Name + " plain mine");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(plainRequest.ToString(), response.Body);
                Assert.DoesNotContain(hostRequest.ToString(), response.Body);

                response = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Notifications/preferences", output, store.Name + " plain preferences");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.DoesNotContain("\"sv\"", response.Body); // the host's saved locale is not the registered user's

                response = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Notifications/log", output, store.Name + " plain log");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                Assert.DoesNotContain(hostRequest.ToString(), response.Body);

                var post = await PostJsonAsync(app, plainClient, plainCookies, Host, plain.UserId, "/api/Notifications/templates/preview", previewBody, output, store.Name + " plain preview");
                Assert.Equal(HttpStatusCode.Forbidden, post.Status);

                response = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Notifications/channels", output, store.Name + " plain channels");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);

                // ---- 4. AN ADMINISTRATOR: their own list, the whole send log, and the preview ----------------------------
                response = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Notifications/mine", output, store.Name + " host mine");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(hostRequest.ToString(), response.Body);
                Assert.DoesNotContain(plainRequest.ToString(), response.Body);

                response = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Notifications/preferences", output, store.Name + " host preferences");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains("\"sv\"", response.Body);

                response = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Notifications/log", output, store.Name + " host log");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(hostRequest.ToString(), response.Body);
                Assert.Contains(plainRequest.ToString(), response.Body);

                post = await PostJsonAsync(app, hostClient, hostCookies, Host, hostUserId, "/api/Notifications/templates/preview", previewBody, output, store.Name + " host preview");
                Assert.Equal(HttpStatusCode.OK, post.Status);
                Assert.Contains("&lt;b&gt;there&lt;/b&gt;", post.Body.Replace("\\u0026", "&").Replace("\\u003C", "<").Replace("\\u003E", ">"));

                // ---- 5. ANONYMOUS: 403 on every surface --------------------------------------------------------------------
                response = await GateDrive.GetAsync(anonymousClient, null, Host, "/api/Notifications/mine", output, store.Name + " anonymous mine");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                response = await GateDrive.GetAsync(anonymousClient, null, Host, "/api/Notifications/preferences", output, store.Name + " anonymous preferences");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                response = await GateDrive.GetAsync(anonymousClient, null, Host, "/api/Notifications/log", output, store.Name + " anonymous log");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);

                // ---- 6. REVOKE view-log FROM ADMINISTRATORS OVER HTTP: a DENY row, not zero rows -----
                var revoked = GateDrive.Clone(definition);
                var adminLogRows = revoked.PermissionList.Where(row => IsKeyRow(row, "view-log", RoleNames.Admin, true)).ToList();
                Assert.NotEmpty(adminLogRows);
                foreach (var row in adminLogRows)
                {
                    row.IsAuthorized = false;
                }

                Assert.Equal(HttpStatusCode.OK,
                    await GateDrive.PutDefinitionAsync(app, hostClient, hostCookies, Host, hostUserId, revoked, output, store.Name + " revoke view-log"));
                var afterRevoke = await GateDrive.RowsAsync(app, Host, NotificationsPermissions.Resource, "view-log");
                output.WriteLine($"[{store.Name}] notifications:view-log rows after revoke = {GateDrive.Describe(afterRevoke)}");
                Assert.Contains(afterRevoke, row => row.RoleName == RoleNames.Admin && !row.IsAuthorized);

                response = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Notifications/log", output, store.Name + " host log (revoked)");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                Assert.DoesNotContain(plainRequest.ToString(), response.Body);

                // ...on the very next request, and the lazy reconciliation did not re-grant it on the read above.
                afterRevoke = await GateDrive.RowsAsync(app, Host, NotificationsPermissions.Resource, "view-log");
                Assert.DoesNotContain(afterRevoke, row => row.RoleName == RoleNames.Admin && row.IsAuthorized);

                // ...and view-own was not collateral damage.
                response = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Notifications/mine", output, store.Name + " host mine (after revoke)");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(hostRequest.ToString(), response.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static bool IsKeyRow(Permission row, string operation, string role, bool authorized) =>
            row.EntityName == NotificationsPermissions.Resource
            && row.PermissionName == operation
            && row.EntityId == PermissionKeys.KeyEntityId
            && row.RoleName == role
            && row.IsAuthorized == authorized;

        private static Task<ModuleDefinition> NotificationsDefinitionAsync(WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
                (await sp.GetRequiredService<Stomme.Repository.IModuleDefinitionRepository>().GetModuleDefinitionsAsync(alias.SiteId))
                    .Single(item => item.Name == "Notifications"));

        private static Task<Guid> DispatchToAsync(WebApplication app, string host, int userId) =>
            GateDrive.InTenantAsync(app, host, (sp, alias) => sp.GetRequiredService<INotificationDispatcher>().DispatchAsync(
                new NotificationDispatchRequest("generic.message",
                    new[] { new DispatchRecipient(NotificationRecipientKind.User, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
                    new[] { NotificationChannelKind.InApp },
                    new[] { new DispatchParameter("subject", "for " + userId), new DispatchParameter("body", "hello") })));

        private static Task<bool> SaveHostPreferenceAsync(WebApplication app, string host, int userId) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
            {
                var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                var preference = NotificationPreferenceAggregate.Create(Guid.NewGuid(), tenantId, alias.SiteId, userId);
                preference.Configure(Array.Empty<ChannelKind>(), NotificationSeverityFilter.All, Array.Empty<string>(), null, DigestMode.Immediate, "sv");
                await sp.GetRequiredService<INotificationPreferenceRepository>().AddAsync(preference);
                await sp.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>().SaveChangesAsync();
                return true;
            });

        private static Task<Guid> GenericTemplateIdAsync(WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
            {
                var template = await sp.GetRequiredService<INotificationTemplateRepository>().GetByKeyAsync(alias.SiteId, "generic.message");
                Assert.True(template != null, "generic.message was not reconciled onto the fresh site");
                return template!.Id;
            });

        /// <summary>A POST carrying the caller's Identity cookie and a matched antiforgery pair - the way a browser performs it.</summary>
        private static async Task<(HttpStatusCode Status, string Body)> PostJsonAsync<T>(
            WebApplication app, HttpClient client, System.Net.CookieContainer cookies, string host, int userId, string path, T body, ITestOutputHelper output, string label)
        {
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + host), path);
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
            var cookieHeader = cookies.GetCookieHeader(uri);
            request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(cookieHeader) ? antiforgeryCookie : cookieHeader + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);

            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[{label}] POST {path} -> {(int)response.StatusCode} {GateDrive.Truncate(text)}");
            return (response.StatusCode, text);
        }
    }
}