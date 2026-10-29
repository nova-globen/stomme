using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Jint;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests
{
    /// <summary>
    /// A theme's colour modes (reported by the Didin session on 10.5.0-preview.2): a theme declares the mode it is
    /// designed for, the host fixes every page it renders to that mode before the first paint, and a theme's own <c>:root</c>
    /// decides its surfaces in both modes.
    /// </summary>
    /// <remarks>
    /// Two gaps, one row. app.css keyed its light tokens on <c>html[data-theme="light"]</c>, which outranks a theme's bare
    /// <c>:root</c>, so a theme lost its light surfaces; and a dark-only theme had no way to say so, so Didin shipped a head script
    /// writing the visitor's stored choice. The script here is App.razor's own, run in Jint against a stand-in document and
    /// storage - what it does before the first paint is the point, and no HTTP or bUnit test runs it.
    /// </remarks>
    public sealed class ThemeColorModeTests
    {
        [Theory]
        [InlineData("dark", "dark")]
        [InlineData(" Dark ", "dark")]
        [InlineData("LIGHT", "light")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("sepia", "")]
        public void A_declared_mode_is_dark_light_or_follows_the_visitor(string declared, string applied) =>
            Assert.Equal(applied, ThemeColorModes.Normalize(declared));

        [Fact]
        public void The_mode_is_the_one_declared_by_the_theme_that_renders_the_page()
        {
            var themes = new List<Theme>
            {
                new() { ThemeName = "Acme.Night", ColorMode = "dark", Themes = new() { new ThemeControl { TypeName = "Acme.Night.Default, Acme.Night" } } },
                new() { ThemeName = "Acme.Day", Themes = new() { new ThemeControl { TypeName = "Acme.Day.Default, Acme.Day" } } },
            };

            Assert.Equal("dark", ThemeColorModes.Of(themes, "Acme.Night.Default, Acme.Night"));
            Assert.Equal("", ThemeColorModes.Of(themes, "Acme.Day.Default, Acme.Day"));
            Assert.Equal("", ThemeColorModes.Of(themes, "Nobody.Default, Nobody"));
            Assert.Equal("", ThemeColorModes.Of(null, "Acme.Night.Default, Acme.Night"));
        }

        [Fact]
        public void The_declared_mode_travels_to_the_client_with_the_theme()
        {
            var themes = new List<Theme> { new() { ThemeName = "Acme.Night", ColorMode = "dark", IsEnabled = true, Themes = new(), Containers = new() } };

            var client = ((IThemeRepository)null).FilterThemes(themes);

            Assert.Equal("dark", Assert.Single(client).ColorMode);
        }

        [Theory]
        [InlineData("src", "Stomme.Server", "wwwroot", "css", "app.css")]
        [InlineData("src", "Stomme.Maui", "wwwroot", "css", "app.css")]
        public void The_framework_tokens_weigh_nothing_so_a_theme_decides_both_modes(params string[] path)
        {
            var css = File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(path).ToArray()));

            Assert.DoesNotMatch(@"(?m)^:root\s*\{", css); // a bare :root outranks nothing a theme writes, but the light block did
            Assert.Matches(@"(?m)^:where\(:root\)\s*\{", css);
            Assert.Matches(@"(?m)^:where\(html\[data-theme=""light""\], body\[mode=""light""\]\)\s*\{", css);
            Assert.DoesNotMatch(@"(?m)^html\[data-theme=""light""\]", css);
            Assert.Matches(@":where\(html\[data-theme=""dark""\]\)\s*\{\s*color-scheme:\s*dark;", css);
            Assert.Matches(@":where\(html\[data-theme=""light""\]\)\s*\{\s*color-scheme:\s*light;", css);
            Assert.Matches(@"html\[data-theme-fixed\] \.db-theme-toggle\s*\{\s*display:\s*none;", css);
        }

        [Fact]
        public void Every_colour_toggle_hides_under_a_fixed_mode()
        {
            var root = RepositoryRoot();
            var toggles = new[] { "src", "examples" }
                .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.razor", SearchOption.AllDirectories))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"<[a-z]+[^>]*stommeTheme\.toggle\(\)[^>]*>").Select(match => (file, tag: match.Value)))
                .ToList();

            Assert.NotEmpty(toggles); // the Graphite and Stomme themes render one
            Assert.All(toggles, toggle => Assert.True(toggle.tag.Contains("db-theme-toggle"), $"{toggle.file}: {toggle.tag} lacks the db-theme-toggle class app.css hides under a fixed mode."));
        }

        [Fact]
        public void A_fixed_mode_wins_over_the_visitors_stored_choice_before_the_first_paint()
        {
            var page = Page.Load(fixedMode: "dark", stored: "{\"mode\":\"light\"}");

            Assert.Equal("dark", page.Attribute("data-theme"));
            Assert.Equal("dark", page.Attribute("data-theme-mode"));
            Assert.Equal("dark", page.Attribute("data-bs-theme"));
            Assert.Equal("dark", page.ColorScheme);
            Assert.Equal("dark", page.FluentStoredMode); // FluentUI starts in the page's mode, not the system's
        }

        [Fact]
        public void A_browser_that_refuses_storage_still_gets_the_mode()
        {
            Assert.Equal("dark", Page.Load(fixedMode: "dark", storageBlocked: true).Attribute("data-theme"));
            Assert.Equal("light", Page.Load(storageBlocked: true).Attribute("data-theme")); // the system's, light here
        }

        [Fact]
        public void Without_a_fixed_mode_the_visitor_decides_as_before()
        {
            var stored = Page.Load(stored: "{\"mode\":\"dark\"}");
            Assert.Equal("dark", stored.Attribute("data-theme"));
            Assert.Equal("dark", stored.FluentStoredMode);

            var system = Page.Load(systemDark: true);
            Assert.Equal("dark", system.Attribute("data-theme"));
            Assert.Equal("system", system.Attribute("data-theme-mode"));
            Assert.Null(system.FluentStoredMode); // FluentUI follows the system itself

            system.Run("stommeTheme.toggle()");
            Assert.Equal("light", system.Attribute("data-theme"));
            Assert.Equal("{\"mode\":\"light\"}", system.Stored("stomme-theme"));
        }

        [Fact]
        public void The_toggle_changes_nothing_under_a_fixed_mode()
        {
            var page = Page.Load(fixedMode: "dark");

            page.Run("stommeTheme.toggle()");

            Assert.Equal("dark", page.Attribute("data-theme"));
            Assert.Null(page.Stored("stomme-theme"));
        }

        [Fact]
        public void A_page_moving_to_another_theme_takes_its_mode_and_gives_it_back()
        {
            var page = Page.Load(stored: "{\"mode\":\"dark\"}");

            page.Run("stommeTheme.fix('light')");
            Assert.Equal("light", page.Attribute("data-theme-fixed"));
            Assert.Equal("light", page.Attribute("data-theme"));

            page.Run("stommeTheme.fix('')");
            Assert.Null(page.Attribute("data-theme-fixed"));
            Assert.Equal("dark", page.Attribute("data-theme")); // the visitor's choice again
        }

        [Fact]
        public void FluentUI_once_started_is_handed_the_fixed_mode()
        {
            var page = Page.Load(fixedMode: "light", systemDark: true);
            page.Run("window.Blazor = { theme: { setThemeMode: function (mode) { __fluentModes.push(mode); } } }; stommeTheme.sync();");

            Assert.Equal("light", page.Evaluate("__fluentModes.join(',')"));
        }

        /// <summary>App.razor's head script, run in Jint against a stand-in &lt;html&gt;, storage, media query and observer.</summary>
        private sealed class Page
        {
            private const string Prelude = """
                var window = globalThis;
                var __attrs = {}, __store = {}, __observers = [], __fluentModes = [];
                var __blocked = false, __systemDark = false;
                function __notify(name) { __observers.forEach(function (o) { if (!o.filter || o.filter.indexOf(name) >= 0) { o.cb([]); } }); }
                var document = { documentElement: {
                    style: {},
                    getAttribute: function (n) { return Object.prototype.hasOwnProperty.call(__attrs, n) ? __attrs[n] : null; },
                    setAttribute: function (n, v) { __attrs[n] = String(v); __notify(n); },
                    removeAttribute: function (n) { delete __attrs[n]; __notify(n); } } };
                var localStorage = {
                    getItem: function (k) { if (__blocked) { throw new Error('SecurityError'); } return Object.prototype.hasOwnProperty.call(__store, k) ? __store[k] : null; },
                    setItem: function (k, v) { if (__blocked) { throw new Error('SecurityError'); } __store[k] = String(v); } };
                window.matchMedia = function () { return { matches: __systemDark, addEventListener: function () {} }; };
                function MutationObserver(cb) { this.cb = cb; }
                MutationObserver.prototype.observe = function (target, options) { __observers.push({ cb: this.cb, filter: options.attributeFilter }); };
                """;

            private readonly Jint.Engine _engine;

            private Page(Jint.Engine engine) => _engine = engine;

            public static Page Load(string fixedMode = null, string stored = null, bool storageBlocked = false, bool systemDark = false)
            {
                var engine = new Jint.Engine();
                engine.Execute(Prelude);
                engine.Execute($"var __fixed = {System.Text.Json.JsonSerializer.Serialize(fixedMode)}, __seed = {System.Text.Json.JsonSerializer.Serialize(stored)};");
                engine.Execute($"__systemDark = {(systemDark ? "true" : "false")};");
                engine.Execute("if (__fixed) { __attrs['data-theme-fixed'] = __fixed; } if (__seed) { __store['stomme-theme'] = __seed; }");
                engine.Execute($"__blocked = {(storageBlocked ? "true" : "false")};");
                engine.Execute(Script.Value);
                return new Page(engine);
            }

            public string Attribute(string name) => Evaluate($"document.documentElement.getAttribute('{name}')");

            public string ColorScheme => Evaluate("document.documentElement.style.colorScheme");

            public string FluentStoredMode => Evaluate("(function () { var s = __store['fluentui-blazor:theme-settings']; return s ? (JSON.parse(s).mode || null) : null; })()");

            public string Stored(string key) => Evaluate($"__store['{key}'] === undefined ? null : __store['{key}']");

            public void Run(string code) => _engine.Execute(code);

            public string Evaluate(string expression)
            {
                var value = _engine.Evaluate(expression);
                return value.IsNull() || value.IsUndefined() ? null : value.ToString();
            }

            private static readonly Lazy<string> Script = new(() =>
            {
                var razor = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "Components", "App.razor"));
                var start = razor.IndexOf("<script>", razor.IndexOf("<base href=\"/\" />", StringComparison.Ordinal), StringComparison.Ordinal) + "<script>".Length;
                return razor.Substring(start, razor.IndexOf("</script>", start, StringComparison.Ordinal) - start);
            });
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
        }
    }
}
