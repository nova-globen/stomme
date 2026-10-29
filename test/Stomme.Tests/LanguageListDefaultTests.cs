using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using LanguagesIndex = Stomme.Modules.Admin.Languages.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests
{
    /// <summary>
    /// The Languages list's Default column showed "Permission Disabled" on every row, so
    /// it never said which language is the site's default. It drew the value through <c>TriStateCheckBox</c> with
    /// <c>Disabled="true"</c>, and that control draws a disabled box as the disabled icon whatever its value - right for
    /// the permission grid, where disabled means "you may not change this", and no use for a read-only column.
    /// </summary>
    public sealed class LanguageListDefaultTests : BunitContext
    {
        private readonly ListedLanguages _languages = new();

        public LanguageListDefaultTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } });
            Services.AddSingleton(HelpControlClickTests.Inert<ILogService>.Create());
            Services.AddSingleton(HelpControlClickTests.Inert<ILocalizationService>.Create());
            Services.AddSingleton(HelpControlClickTests.Inert<IPackageService>.Create());
            Services.AddSingleton(HelpControlClickTests.Inert<IUserService>.Create());
            Services.AddSingleton<ILanguageService>(_languages);
            Services.AddLocalization();
            Services.AddFluentUIComponents(); // the pager's search box
        }

        [Fact]
        public void The_Default_column_marks_the_default_language_and_only_it()
        {
            _languages.Listed.Add(new Language { LanguageId = 0, Code = "en", Name = "English", IsDefault = false });
            _languages.Listed.Add(new Language { LanguageId = 1, SiteId = 1, Code = "sv", Name = "Swedish", IsDefault = true });

            var cut = Render<LanguagesIndex>(parameters => parameters
                .AddCascadingValue(Admin())
                .AddCascadingValue(new ModuleModel { ModuleId = 20, SiteId = 1, ModuleType = "Stomme.Modules.Admin.Languages.Index, Stomme.Client", PermissionList = new List<Permission>() }));

            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr").Count));
            var rows = cut.FindAll("tbody tr").ToDictionary(row => row.Children[3].TextContent.Trim(), row => row.Children[4]);

            // The two cells must say different things, and neither may be the disabled icon that says nothing. (Compared by
            // what they draw: the markup differs anyway, by the ids of a disabled box's dead click handlers.)
            static string Drawn(AngleSharp.Dom.IElement cell) => string.Join(" ", cell.QuerySelectorAll("img").Select(img => img.GetAttribute("src")));
            Assert.NotEqual(Drawn(rows["en"]), Drawn(rows["sv"]));
            Assert.DoesNotContain("disabled.png", rows["en"].InnerHtml + rows["sv"].InnerHtml);
            Assert.NotNull(rows["sv"].QuerySelector("img[src='images/checked.png']"));
            Assert.Null(rows["en"].QuerySelector("img"));
        }

        private static PageState Admin() => new()
        {
            Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" },
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 20, SiteId = 1, Path = "admin/languages", Name = "Languages" },
            User = new User { UserId = 1, Username = "admin", Roles = ";" + RoleNames.Admin + ";" },
            RenderMode = RenderModes.Interactive,
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
        };

        private sealed class ListedLanguages : ILanguageService
        {
            public List<Language> Listed { get; } = new();

            public Task<List<Language>> GetLanguagesAsync(int siteId) => GetLanguagesAsync(siteId, "");

            public Task<List<Language>> GetLanguagesAsync(int siteId, string packageName) => Task.FromResult(Listed.ToList());

            public Task<Language> GetLanguageAsync(int languageId) => Task.FromResult(Listed.Single(language => language.LanguageId == languageId));

            public Task<Language> AddLanguageAsync(Language language) => throw new NotSupportedException();

            public Task EditLanguageAsync(Language language) => throw new NotSupportedException();

            public Task DeleteLanguageAsync(int languageId) => throw new NotSupportedException();
        }
    }
}
