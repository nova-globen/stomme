using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Bunit;
using Stomme.Formats;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules.Formats.Services;
using Stomme.Modules.Notifications.Services;
using Stomme.Notifications;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ChannelsPage = Stomme.Modules.Notifications.Channels;
using FormatsPage = Stomme.Modules.Formats.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-22's depth-2 pass (2026-10-03) - a page that opens an editor below its list brings the editor into view. On Formats
    /// the editor sat 427 pixels below a 900-pixel viewport once the list held the presets, so Edit looked like it did
    /// nothing; Connections, Datasets, Dashboards, help topics and notification channels and templates had the same shape.
    /// <c>ModuleBase.BringIntoView</c> asks the browser to scroll after the next render.
    /// </summary>
    public sealed class BringIntoViewTests : BunitContext
    {
        private const string ScrollToId = "Stomme.Interop.scrollToId";

        public BringIntoViewTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } });
            Services.AddSingleton(HelpControlClickTests.Inert<ILogService>.Create());
            Services.AddLocalization(); // the rows' delete confirmations
        }

        [Fact]
        public void Formats_Edit_brings_the_editor_into_view()
        {
            Services.AddSingleton<IFormatsService>(new ListedFormats());
            var cut = Render<FormatsPage>(parameters => parameters
                .AddCascadingValue(Admin("admin/formats"))
                .AddCascadingValue(Module("Stomme.Modules.Formats.Index, Stomme.Client", FormatPermissions.Declarations)));

            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
            Assert.Empty(ScrollsTo("formats-editor"));

            cut.FindAll("tbody tr button").First(button => button.TextContent.Trim() == "Edit").Click();

            cut.WaitForAssertion(() => Assert.Single(ScrollsTo("formats-editor")));
            Assert.Equal(ListedFormats.Own.Name, cut.Find("#formats-name").GetAttribute("value"));
        }

        [Fact]
        public void A_channel_editor_is_brought_into_view_only_once_it_has_rendered()
        {
            // The editor appears only when the channel has been read, after the handler's await. Held open here: a scroll asked
            // for before it completes would name an element the page does not have yet, and scroll nothing.
            var read = new TaskCompletionSource<NotificationChannelDetail>();
            Services.AddSingleton(ScriptedChannels.Create(read.Task));
            var cut = Render<ChannelsPage>(parameters => parameters
                .AddCascadingValue(Admin("admin/notifications"))
                .AddCascadingValue(Module("Stomme.Modules.Notifications.Channels, Stomme.Client", NotificationsPermissions.Declarations)));

            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
            cut.FindAll("tbody tr button").First(button => button.TextContent.Trim() == "Edit").Click();

            Assert.Empty(cut.FindAll("#notifications-channel-editor"));
            Assert.Empty(ScrollsTo("notifications-channel-editor"));

            read.SetResult(new NotificationChannelDetail(ScriptedChannels.ChannelId, NotificationChannelKind.Email, "Operations mail", true, null, null, Array.Empty<NotificationChannelOption>(), null));

            cut.WaitForAssertion(() => Assert.Single(ScrollsTo("notifications-channel-editor")));
            Assert.NotNull(cut.Find("#notifications-channel-editor"));
        }

        [Fact]
        public void Opening_a_channel_clears_the_last_saves_message()
        {
            // Driven: "Channel saved." stayed above the form while the next channel was being edited.
            Services.AddSingleton(ScriptedChannels.Create(Task.FromResult(new NotificationChannelDetail(ScriptedChannels.ChannelId, NotificationChannelKind.Email, "Operations mail", true, null, null, Array.Empty<NotificationChannelOption>(), null))));
            var cut = Render<ChannelsPage>(parameters => parameters
                .AddCascadingValue(Admin("admin/notifications"))
                .AddCascadingValue(Module("Stomme.Modules.Notifications.Channels, Stomme.Client", NotificationsPermissions.Declarations)));

            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
            cut.Find("#notifications-channel-new").Click();
            cut.Find("#notifications-channel-save").Click(); // this service refuses every save
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#notifications-channels-message")));

            cut.FindAll("tbody tr button").First(button => button.TextContent.Trim() == "Edit").Click();

            cut.WaitForAssertion(() => Assert.Equal("Operations mail", cut.Find("#notifications-channel-name").GetAttribute("value")));
            Assert.Empty(cut.FindAll("#notifications-channels-message"));
        }

        private IReadOnlyList<JSRuntimeInvocation> ScrollsTo(string elementId) =>
            JSInterop.Invocations.Where(invocation => invocation.Identifier == ScrollToId && Equals(invocation.Arguments.FirstOrDefault(), elementId)).ToList();

        private static PageState Admin(string path) => new()
        {
            Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" },
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 9, SiteId = 1, Path = path, Name = "Admin" },
            User = new User { UserId = 1, Username = "admin", Roles = ";" + RoleNames.Admin + ";" },
            RenderMode = RenderModes.Interactive,
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
        };

        /// <summary>The module's keys as its definition seeds them, so the page's manage buttons render for an administrator.</summary>
        private static ModuleModel Module(string moduleType, IReadOnlyList<(string Key, string[] Roles)> declarations) => new()
        {
            ModuleId = 12,
            SiteId = 1,
            ModuleType = moduleType,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(declarations))
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Admin, true) })
                .ToList(),
        };

        private sealed class ListedFormats : IFormatsService
        {
            public static readonly FormatDescriptor Own = new(Guid.Parse("4f1c2a77-0000-4000-8000-000000000001"), "Drive date", FormatKind.Date, "yyyy-MM-dd", "sv-SE", FormatScope.Tenant, string.Empty);

            public Task<List<FormatDescriptor>> ListAsync(FormatKind? kind = null, FormatScope? scope = null) =>
                Task.FromResult(new List<FormatDescriptor>
                {
                    new(Guid.Parse("4f1c2a77-0000-4000-8000-000000000002"), "ISO 8601 date", FormatKind.Date, "yyyy-MM-dd", string.Empty, FormatScope.System, string.Empty),
                    Own,
                });

            public Task<FormatDescriptor> GetAsync(Guid id) => Task.FromResult(Own);

            public Task<FormatDescriptor> SaveAsync(FormatDefinitionRequest request) => throw new NotSupportedException();

            public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        }

        /// <summary>One channel in the list; reading it waits on the test. Everything else answers nothing.</summary>
        public class ScriptedChannels : DispatchProxy
        {
            public static readonly Guid ChannelId = Guid.Parse("4f1c2a77-0000-4000-8000-000000000003");

            private static readonly INotificationsService Nothing = HelpControlClickTests.Inert<INotificationsService>.Create();

            private Task<NotificationChannelDetail> _read;

            public static INotificationsService Create(Task<NotificationChannelDetail> read)
            {
                var proxy = Create<INotificationsService, ScriptedChannels>();
                ((ScriptedChannels)(object)proxy)._read = read;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args) => targetMethod.Name switch
            {
                nameof(INotificationsService.GetChannelsAsync) => Task.FromResult(new List<NotificationChannelSummary> { new(ChannelId, NotificationChannelKind.Email, "Operations mail", true) }),
                nameof(INotificationsService.GetChannelAsync) => _read,
                _ => targetMethod.Invoke(Nothing, args),
            };
        }
    }
}
