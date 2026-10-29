using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ModuleModel = Stomme.Models.Module;
using ThemeEdit = Stomme.Modules.Admin.Themes.Edit;

namespace Stomme.Tests
{
    /// <summary>
    /// The theme page shows the theme's internal name. It never read it from the loaded
    /// theme, so "Internal Name" was blank for every theme and a licence given as a "~" path linked to "/Themes/" with no
    /// theme in it. Driven: Graphite's page empty while <c>api/Theme/1</c> returned its type name.
    /// </summary>
    public sealed class ThemeEditInformationTests : BunitContext
    {
        private const string GraphiteType = "Stomme.Themes.GraphiteTheme, Stomme.Client";

        public ThemeEditInformationTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton(HelpControlClickTests.Inert<ILogService>.Create());
            Services.AddSingleton(HelpControlClickTests.Inert<IPackageService>.Create());
            Services.AddSingleton(EmptyLists<IRoleService>.Create());
            Services.AddSingleton(EmptyLists<IUserService>.Create());
            Services.AddSingleton(EmptyLists<IUserRoleService>.Create());
            Services.AddSingleton<IThemeService>(OneTheme.Create(new Theme
            {
                ThemeId = 1,
                Name = "Graphite",
                ThemeName = GraphiteType,
                Version = "1.0.0",
                IsEnabled = true,
                PackageName = string.Empty,
                Owner = string.Empty,
                Url = string.Empty,
                Contact = string.Empty,
                License = "~/license.txt",
                Fingerprint = string.Empty,
                PermissionList = new List<Permission>(),
            }));
            Services.AddLocalization();
        }

        [Fact]
        public void The_information_section_shows_the_themes_internal_name_and_its_licence_links_to_the_theme()
        {
            var cut = Render<ThemeEdit>(parameters => parameters
                .AddCascadingValue(Host())
                .AddCascadingValue(new ModuleModel { ModuleId = 25, SiteId = 1, ModuleType = "Stomme.Modules.Admin.Themes.Edit, Stomme.Client", PermissionList = new List<Permission>() }));

            cut.WaitForAssertion(() => Assert.Equal(GraphiteType, cut.Find("#themename").GetAttribute("value")));

            var licence = cut.Find("a[target=_new]").GetAttribute("href");
            Assert.Equal(Alias().BaseUrl + "/Themes/" + Utilities.GetTypeName(GraphiteType) + "/license.txt", licence);
            Assert.Contains("GraphiteTheme", licence);
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Host() => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 25, SiteId = 1, Path = "admin/themes", Name = "Themes" },
            User = new User { UserId = 1, Username = "host", Roles = ";" + RoleNames.Host + ";" + RoleNames.Admin + ";" },
            RenderMode = RenderModes.Interactive,
            QueryString = new Dictionary<string, string> { ["id"] = "1" },
            UrlParameters = "",
        };

        /// <summary>Answers a list with an empty one and anything else with nothing.</summary>
        public class EmptyLists<T> : DispatchProxy
            where T : class
        {
            private static readonly T Nothing = HelpControlClickTests.Inert<T>.Create();

            public static T Create() => Create<T, EmptyLists<T>>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var returns = targetMethod.ReturnType;
                if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var inner = returns.GetGenericArguments()[0];
                    if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { Activator.CreateInstance(inner) });
                    }
                }

                return targetMethod.Invoke(Nothing, args);
            }
        }

        /// <summary>The one theme the page reads; anything else answers nothing.</summary>
        public class OneTheme : DispatchProxy
        {
            private static readonly IThemeService Nothing = HelpControlClickTests.Inert<IThemeService>.Create();

            private Theme _theme;

            public static IThemeService Create(Theme theme)
            {
                var proxy = Create<IThemeService, OneTheme>();
                ((OneTheme)(object)proxy)._theme = theme;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args) =>
                targetMethod.Name == nameof(IThemeService.GetThemeAsync) ? Task.FromResult(_theme) : targetMethod.Invoke(Nothing, args);
        }
    }
}
