using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Help;
using Stomme.Models;
using Stomme.Models.Help;
using Stomme.Modules.ContextualHelp.Services;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Themes.Controls;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HelpIndex = Stomme.Modules.ContextualHelp.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// BL-84 increments 7-8: the help controls are CLICKED - the launcher in both render modes, and the admin
    /// control's editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Razor file compiled without <c>@using Microsoft.AspNetCore.Components.Web</c> turns <c>@onclick</c> into a plain
    /// attribute: it builds green, renders, and every click is dead with no console error. No SSR or HTTP drive can see
    /// that; bUnit's <c>Click()</c> throws on an element with no bound handler, so these facts go red on exactly that.
    /// </para>
    /// <para>
    /// <b>The launcher's two shapes are the point of D103 refutation 3.</b> On an INTERACTIVE page it is a drawer that a
    /// click opens. On a STATIC page it is a native <c>&lt;details&gt;</c> with no handler at all - which is what lets an
    /// anonymous page view show help without a circuit.
    /// </para>
    /// </remarks>
    public sealed class HelpControlClickTests : BunitContext
    {
        private const string UsersControl = "Stomme.Modules.Admin.Users.Index, Stomme.Client";
        private const string UsersAnchor = "stomme.modules.admin.users.index";

        private readonly RecordingHelpService _service = new();

        public HelpControlClickTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IHelpService>(_service);
            Services.AddLocalization(); // the delete's confirmation dialog is localized
        }

        // ---- the launcher ----------------------------------------------------------------------------------------------

        [Fact]
        public void On_an_INTERACTIVE_page_the_launcher_is_a_drawer_that_a_CLICK_opens()
        {
            _service.Topics[UsersAnchor] = new HelpTopicView(UsersAnchor, "en", "User Management", "<p>The <strong>accounts</strong>.</p>");

            var cut = Render<HelpLauncher>(parameters => parameters.AddCascadingValue(Launch(RenderModes.Interactive)));

            // It asked for the PAGE and then each CONTROL, normalized.
            cut.WaitForAssertion(() => Assert.Equal(new[] { "page:admin/users", UsersAnchor }, _service.Asked));

            var drawer = cut.Find(".db-drawer");
            Assert.DoesNotContain("show", drawer.ClassList);
            Assert.Empty(cut.FindAll("details"));

            cut.Find(".db-help-trigger").Click();
            Assert.Contains("show", cut.Find(".db-drawer").ClassList);
            Assert.Equal("User Management", cut.Find(".db-help-title").TextContent);
            Assert.NotNull(cut.Find(".db-help-topic strong"));                 // the server's HTML, rendered as markup

            // The backdrop and the close button are bound too.
            cut.Find(".db-drawer-backdrop").Click();
            Assert.DoesNotContain("show", cut.Find(".db-drawer").ClassList);
        }

        [Fact]
        public void On_a_STATIC_page_the_launcher_is_a_native_details_with_NO_handler_to_need_a_circuit()
        {
            _service.Topics["page:admin/users"] = new HelpTopicView("page:admin/users", "en", "Users page", "<p>Page help.</p>");

            var cut = Render<HelpLauncher>(parameters => parameters.AddCascadingValue(Launch(RenderModes.Static)));

            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("details.db-help")));
            Assert.Empty(cut.FindAll("button"));                                 // nothing that needs an event handler
            Assert.Empty(cut.FindAll(".db-drawer"));
            Assert.Equal("Users page", cut.Find("details.db-help .db-help-title").TextContent);
        }

        [Fact]
        public void With_no_topic_or_a_failing_service_the_launcher_renders_NOTHING()
        {
            var none = Render<HelpLauncher>(parameters => parameters.AddCascadingValue(Launch(RenderModes.Interactive)));
            none.WaitForAssertion(() => Assert.Equal(2, _service.Asked.Count));
            Assert.Equal(string.Empty, none.Markup.Trim());

            // Help is never worth a broken header: an exception (a denied caller, an unreachable server) is "no topic".
            _service.Throw = true;
            var failing = Render<HelpLauncher>(parameters => parameters.AddCascadingValue(Launch(RenderModes.Static)));
            Assert.Equal(string.Empty, failing.Markup.Trim());
        }

        // The server says whether Help is on, in the site's settings; with it off the launcher asks nothing - each ask
        // was a 404 in a WebAssembly site's console on every page - and a server that does not say is asked as before.
        [Fact]
        public void With_Help_switched_off_the_launcher_asks_NOTHING_and_renders_nothing()
        {
            _service.Topics["page:admin/users"] = new HelpTopicView("page:admin/users", "en", "Users page", "<p>Page help.</p>");

            var off = Launch(RenderModes.Interactive);
            off.Site.Settings = new Dictionary<string, string> { ["Help:Enabled"] = "false" };
            var silent = Render<HelpLauncher>(parameters => parameters.AddCascadingValue(off));
            Assert.Empty(_service.Asked);
            Assert.Equal(string.Empty, silent.Markup.Trim());

            var on = Launch(RenderModes.Interactive);
            on.Site.Settings = new Dictionary<string, string> { ["Help:Enabled"] = "true" };
            var shown = Render<HelpLauncher>(parameters => parameters.AddCascadingValue(on));
            shown.WaitForAssertion(() => Assert.NotEmpty(_service.Asked));
            shown.WaitForAssertion(() => Assert.NotNull(shown.Find(".db-help-trigger")));
        }

        [Fact]
        public void The_ControlPanel_renders_the_launcher_under_its_OWN_switch_not_the_language_switchers()
        {
            // D103 refutation 3(a): the slot the design first chose was conditional on ShowLanguageSwitcher. No shipped
            // theme turns that off today, so only this render can tell the two placements apart.
            Services.AddSingleton(Inert<IPageService>.Create());
            Services.AddSingleton(Inert<ISettingService>.Create());
            _service.Topics["page:admin/users"] = new HelpTopicView("page:admin/users", "en", "Users page", "<p>Page help.</p>");

            var withoutLanguages = Render<ControlPanel>(parameters => parameters
                .AddCascadingValue(Launch(RenderModes.Static))
                .Add(panel => panel.ShowLanguageSwitcher, false));
            withoutLanguages.WaitForAssertion(() => Assert.NotNull(withoutLanguages.Find("details.db-help")));

            var withoutHelp = Render<ControlPanel>(parameters => parameters
                .AddCascadingValue(Launch(RenderModes.Static))
                .Add(panel => panel.ShowHelp, false)
                .Add(panel => panel.ShowLanguageSwitcher, false));
            Assert.Empty(withoutHelp.FindAll(".db-help"));
        }

        // ---- the admin control -----------------------------------------------------------------------------------------

        [Fact]
        public void The_editor_saves_previews_and_the_list_actions_are_bound()
        {
            var cut = Render<HelpIndex>(parameters => parameters
                .AddCascadingValue(Admin(RoleNames.Admin))
                .AddCascadingValue(HelpModule()));

            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#help-topics tbody tr")));

            cut.Find("#help-new").Click();
            cut.Find("#help-anchor").Input("page:about");
            cut.Find("#help-locale").Input("en");
            cut.Find("#help-title").Input("About");
            cut.Find("#help-markdown").Input("**About** us.");

            cut.Find("#help-preview").Click();
            cut.WaitForAssertion(() => Assert.Equal("**About** us.", _service.Previewed.Single()));
            Assert.NotNull(cut.Find("#help-preview-output strong"));

            cut.Find("#help-save").Click();
            cut.WaitForAssertion(() => Assert.Single(_service.Saved));
            Assert.Equal(new HelpTopicSaveInput(null, "page:about", "en", "About", "**About** us."), _service.Saved[0]);

            var row = cut.FindAll("#help-topics tbody tr").First();
            row.QuerySelectorAll("button").First(button => button.TextContent.Trim() == "Unpublish").Click();
            cut.WaitForAssertion(() => Assert.Equal((RecordingHelpService.ExistingId, false), _service.Published.Single()));

            // A delete asks first - the row's Delete opens the confirmation, and only its own Delete deletes.
            cut.FindAll("#help-topics tbody tr").First().QuerySelectorAll("button").First(button => button.TextContent.Trim() == "Delete").Click();
            Assert.Empty(_service.Deleted);
            cut.Find(".app-actiondialog .db-dialog-foot").QuerySelectorAll("button").First(button => button.TextContent.Trim() == "Delete").Click();
            cut.WaitForAssertion(() => Assert.Equal(RecordingHelpService.ExistingId, _service.Deleted.Single()));
        }

        [Fact]
        public void A_user_without_author_or_manage_gets_the_denial_and_the_service_is_never_called()
        {
            var cut = Render<HelpIndex>(parameters => parameters
                .AddCascadingValue(Admin(RoleNames.Registered))
                .AddCascadingValue(HelpModule()));

            Assert.NotNull(cut.Find("#help-denied"));
            Assert.Equal(0, _service.ListReads);
        }

        // ---- fixtures --------------------------------------------------------------------------------------------------

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Launch(string renderMode) => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site", Pages = new List<Page>() },
            Page = new Page { PageId = 3, SiteId = 1, Path = "admin/users", Name = "Users", PermissionList = new List<Permission>() },
            Modules = new List<ModuleModel> { new() { ModuleId = 5, SiteId = 1, ModuleType = UsersControl, PermissionList = new List<Permission>() } },
            RenderMode = renderMode,
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
        };

        private static PageState Admin(string role) => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 9, SiteId = 1, Path = "admin/help", Name = "Help" },
            User = new User { UserId = 7, Username = "user7", Roles = ";" + role + ";" },
            RenderMode = RenderModes.Interactive, // an admin page is interactive; the delete's dialog takes that path
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
        };

        /// <summary>The key rows as the module definition seeds them: view to Everyone, author and manage to Administrators.</summary>
        private static ModuleModel HelpModule() => new()
        {
            ModuleId = 12,
            SiteId = 1,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(HelpPermissions.Declarations))
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Registered, true) })
                .ToList(),
        };

        private sealed class RecordingHelpService : IHelpService
        {
            public static readonly Guid ExistingId = Guid.Parse("0b6a1f3e-1111-4222-8333-444455556666");

            public readonly Dictionary<string, HelpTopicView> Topics = new(StringComparer.Ordinal);
            public readonly List<string> Asked = new();
            public readonly List<HelpTopicSaveInput> Saved = new();
            public readonly List<string> Previewed = new();
            public readonly List<(Guid, bool)> Published = new();
            public readonly List<Guid> Deleted = new();
            public int ListReads;
            public bool Throw;

            public Task<HelpTopicView> GetTopicAsync(string anchor, string locale = null)
            {
                Asked.Add(anchor);
                if (Throw)
                {
                    throw new InvalidOperationException("the server is unreachable");
                }

                return Task.FromResult(Topics.TryGetValue(anchor, out var topic) ? topic : null);
            }

            public Task<List<HelpTopicSummary>> SearchAsync(string query) => Task.FromResult(new List<HelpTopicSummary>());

            public Task<List<HelpTopicSummary>> GetTopicsAsync()
            {
                ListReads++;
                return Task.FromResult(new List<HelpTopicSummary>
                {
                    new(ExistingId, "page:admin", "en", "Administration", isPublished: true, isSeeded: true, seedContributorId: "stomme"),
                });
            }

            public Task<List<HelpConflictView>> GetConflictsAsync() => Task.FromResult(new List<HelpConflictView>());

            public Task<HelpTopicEditView> GetTopicForEditAsync(Guid id) =>
                Task.FromResult(new HelpTopicEditView(id, "page:about", "en", "About", "**About** us.", false, false, null, 1, false));

            public Task<Guid> SaveTopicAsync(HelpTopicSaveInput input)
            {
                Saved.Add(input);
                return Task.FromResult(Guid.NewGuid());
            }

            public Task SetPublishedAsync(Guid id, bool published)
            {
                Published.Add((id, published));
                return Task.CompletedTask;
            }

            public Task<string> PreviewAsync(string markdown)
            {
                Previewed.Add(markdown);
                return Task.FromResult("<p><strong>About</strong> us.</p>");
            }

            public Task DeleteTopicAsync(Guid id)
            {
                Deleted.Add(id);
                return Task.CompletedTask;
            }

            public Task ResetTopicAsync(Guid id) => Task.CompletedTask;
        }

        /// <summary>An interface implementation that answers every call with nothing - a default, or a completed task of one.</summary>
        public class Inert<T> : System.Reflection.DispatchProxy
            where T : class
        {
            public static T Create() => System.Reflection.DispatchProxy.Create<T, Inert<T>>();

            protected override object Invoke(System.Reflection.MethodInfo targetMethod, object[] args)
            {
                var returns = targetMethod.ReturnType;
                if (returns == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var inner = returns.GetGenericArguments()[0];
                    return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                        .Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
                }

                return returns.IsValueType && returns != typeof(void) ? Activator.CreateInstance(returns) : null;
            }
        }

        private sealed class NoOpLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => Task.FromResult(new List<Log>());
            public Task<Log> GetLogAsync(int logId) => Task.FromResult<Log>(null!);
            public Task DeleteLogsAsync(int siteId) => Task.CompletedTask;
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
        }
    }
}
