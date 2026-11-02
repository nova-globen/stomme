using System.Collections.Generic;
using System.Linq;
using Bunit;
using Stomme.Models;
using Stomme.Modules.Controls;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests
{
    /// <summary>
    /// The list and confirmation controls every admin page uses, from the keyboard (BL-241, from BL-239's wave 2 wC). The
    /// interactive Pager's page links were anchors with no href - Tab never reached them - and its arrows were named only by
    /// a title on a hidden icon; ActionDialog had role="dialog" and nothing else: no name, no Escape, focus left behind.
    /// Driven in a browser too (Tab through the pager, open a delete confirmation, Tab round it, Escape).
    /// </summary>
    public sealed class KeyboardControlsTests : BunitContext
    {
        public KeyboardControlsTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } });
            Services.AddSingleton(HelpControlClickTests.Inert<Stomme.Services.ILogService>.Create());
            Services.AddLocalization();
        }

        [Fact]
        public void The_interactive_pager_is_named_buttons_that_mark_the_current_page()
        {
            var cut = Render<Pager<int>>(parameters => parameters
                .AddCascadingValue(State())
                .AddCascadingValue(Module())
                .Add(pager => pager.Items, Enumerable.Range(1, 25))
                .Add(pager => pager.PageSize, "10")
                .Add(pager => pager.Header, (RenderFragment)(builder => builder.AddMarkupContent(0, "<th>N</th>")))
                .Add(pager => pager.Row, (RenderFragment<int>)(item => builder => builder.AddMarkupContent(0, $"<td>{item}</td>"))));

            var controls = cut.FindAll("button.db-page-link");
            Assert.NotEmpty(controls);
            Assert.All(controls, control => Assert.False(string.IsNullOrWhiteSpace(control.GetAttribute("aria-label"))));
            Assert.Empty(cut.FindAll("a.db-page-link:not([href])")); // an anchor without href is not in the Tab order
            Assert.Equal("1", cut.Find("[aria-current=page]").TextContent.Trim()); // named "Page 1" in the browser; the key here
            Assert.True(cut.Find("button[aria-label='PreviousPage'], button[aria-label='Previous page']").HasAttribute("disabled"));

            cut.Find("button[aria-label='NextPage'], button[aria-label='Next page']").Click();

            Assert.Equal("2", cut.Find("[aria-current=page]").TextContent.Trim());
        }

        // BL-241, driven: an empty list was a bare header row (Url Mappings, Setting Management), which reads as a page that did
        // not load. It says so now, in the page's own words when it gives them.
        [Theory]
        [InlineData(null, "Empty")]
        [InlineData("No URL is mapped yet. Add one to send an old address to a page.", "No URL is mapped yet. Add one to send an old address to a page.")]
        public void An_empty_list_says_so(string? emptyMessage, string said)
        {
            var cut = Render<Pager<int>>(parameters => parameters
                .AddCascadingValue(State())
                .AddCascadingValue(Module())
                .Add(pager => pager.Items, Enumerable.Empty<int>())
                .Add(pager => pager.EmptyMessage, emptyMessage)
                .Add(pager => pager.Header, (RenderFragment)(builder => builder.AddMarkupContent(0, "<th>N</th>")))
                .Add(pager => pager.Row, (RenderFragment<int>)(item => builder => builder.AddMarkupContent(0, $"<td>{item}</td>"))));

            Assert.Equal(said, cut.Find("tbody tr.app-pager-empty").TextContent.Trim()); // the resource key in bUnit, "Nothing here yet." in a site

            var filled = Render<Pager<int>>(parameters => parameters
                .AddCascadingValue(State())
                .AddCascadingValue(Module())
                .Add(pager => pager.Items, new[] { 1 })
                .Add(pager => pager.Row, (RenderFragment<int>)(item => builder => builder.AddMarkupContent(0, $"<td>{item}</td>"))));
            Assert.Empty(filled.FindAll("tr.app-pager-empty"));
        }

        [Fact]
        public void The_action_dialog_is_a_named_modal_that_traps_the_keyboard_and_closes()
        {
            var cut = Render<ActionDialog>(parameters => parameters
                .AddCascadingValue(State())
                .AddCascadingValue(Module())
                .Add(dialog => dialog.Header, "Delete note")
                .Add(dialog => dialog.Message, "Delete the note? This cannot be undone.")
                .Add(dialog => dialog.Action, "Delete")
                .Add(dialog => dialog.Security, SecurityAccessLevel.Anonymous)
                .Add(dialog => dialog.OnClick, () => { }));

            cut.Find("button.db-btn").Click();

            var modal = cut.Find(".db-dialog[role=dialog]");
            Assert.Equal("true", modal.GetAttribute("aria-modal"));
            Assert.Equal("Delete note", cut.Find("#" + modal.GetAttribute("aria-labelledby")).TextContent.Trim());
            Assert.Equal("Delete the note? This cannot be undone.", cut.Find("#" + modal.GetAttribute("aria-describedby")).TextContent.Trim());
            // Focus to Cancel, Tab kept inside and Escape on Close are the browser's half (Stomme.Interop.trapDialog).
            cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Stomme.Interop.trapDialog"));
            Assert.False(string.IsNullOrWhiteSpace(cut.Find(".db-dialog-close").GetAttribute("aria-label")));

            cut.Find(".db-dialog-close").Click();

            Assert.Empty(cut.FindAll(".db-dialog"));
        }

        // C5 (BL-239): OnConfirm is awaited - the work after an await completes before the dialog closes, which a plain
        // Action OnClick (an async lambda run unawaited) never guaranteed.
        [Fact]
        public void The_action_dialog_awaits_OnConfirm_before_it_closes()
        {
            var done = false;
            var gate = new System.Threading.Tasks.TaskCompletionSource();
            var cut = Render<ActionDialog>(parameters => parameters
                .AddCascadingValue(State())
                .AddCascadingValue(Module())
                .Add(dialog => dialog.Header, "Delete note")
                .Add(dialog => dialog.Message, "Delete the note?")
                .Add(dialog => dialog.Action, "Delete")
                .Add(dialog => dialog.Security, SecurityAccessLevel.Anonymous)
                .Add(dialog => dialog.OnConfirm, async () => { await gate.Task; done = true; }));

            cut.Find("button.db-btn").Click();
            cut.Find(".db-dialog-foot button").Click();

            Assert.False(done);
            Assert.NotEmpty(cut.FindAll(".db-dialog")); // still open while the confirmed work runs
            gate.SetResult();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".db-dialog")));
            Assert.True(done);
        }

        private static PageState State() => new()
        {
            Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" },
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 9, SiteId = 1, Path = "admin/notes", Name = "Admin" },
            User = new User { UserId = 1, Username = "admin", Roles = ";" + RoleNames.Admin + ";" },
            RenderMode = RenderModes.Interactive,
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
            Route = new Route("http://localhost/admin/notes", "localhost"),
        };

        private static ModuleModel Module() => new()
        {
            ModuleId = 12,
            PageModuleId = 3,
            SiteId = 1,
            ModuleType = "Stomme.Modules.Formats.Index, Stomme.Client",
            RenderMode = RenderModes.Interactive,
            PermissionList = new List<Permission> { new(PermissionNames.View, RoleNames.Admin, true) },
        };
    }
}
