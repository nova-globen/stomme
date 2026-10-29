using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Modules.Controls;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests
{
    /// <summary>
    /// Enter in a list's search box searches. The box's value binds on its change event,
    /// which fires after keydown, so the Pager's keydown handler read the search before the text had arrived: "Drive" typed,
    /// Enter pressed, the pages list unfiltered until the Search button was clicked. FluentUI's key-press hook commits the value
    /// and then calls the Pager; this drives that hook the way its script does.
    /// </summary>
    public sealed class PagerSearchEnterTests : BunitContext
    {
        public PagerSearchEnterTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } });
            Services.AddSingleton(HelpControlClickTests.Inert<ILogService>.Create());
            Services.AddLocalization();
            Services.AddFluentUIComponents();
        }

        [Fact]
        public async Task Enter_in_the_search_box_filters_the_list_with_what_was_typed()
        {
            var languages = new List<Language>
            {
                new() { LanguageId = 1, Code = "en", Name = "English" },
                new() { LanguageId = 2, Code = "sv", Name = "Swedish" },
            };
            var cut = Render<Pager<Language>>(parameters => parameters
                .AddCascadingValue(Page())
                .AddCascadingValue(new ModuleModel { ModuleId = 20, SiteId = 1, ModuleType = "Stomme.Modules.Admin.Languages.Index, Stomme.Client", RenderMode = RenderModes.Interactive })
                .Add(pager => pager.Items, languages)
                .Add(pager => pager.SearchProperties, "Name")
                .Add(pager => pager.Row, language => $"<td class=\"name\">{language.Name}</td>"));

            Assert.Equal(new[] { "English", "Swedish" }, Names(cut));

            var box = cut.FindComponent<FluentTextInput>();
            await cut.InvokeAsync(() => box.Instance.ChangeAfterKeyPressHandlerAsync("Swed", KeyPress.For(KeyCode.Enter)));

            cut.WaitForAssertion(() => Assert.Equal(new[] { "Swedish" }, Names(cut)));
        }

        private static string[] Names(IRenderedComponent<Pager<Language>> cut) =>
            cut.FindAll("td.name").Select(cell => cell.TextContent).ToArray();

        private static PageState Page() => new()
        {
            Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" },
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 20, SiteId = 1, Path = "admin/languages", Name = "Languages" },
            User = new User { UserId = 1, Username = "admin", Roles = ";" + RoleNames.Admin + ";" },
            RenderMode = RenderModes.Interactive,
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
        };
    }
}
