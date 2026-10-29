using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The dark/light toggle must move EVERY colour system on the page, not just Stomme's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>App.razor</c>'s <c>stommeTheme.apply()</c> is the single source of truth for the colour mode. It
    /// sets <c>data-theme</c> (the <c>--db-*</c> layer) and hands the mode to FluentUI 5. It must ALSO set
    /// Bootstrap 5.3's own <c>data-bs-theme</c>, because <c>BlazorTheme</c> loads Bootstrap from the CDN and
    /// Bootstrap owns its chrome independently.
    /// </para>
    /// <para>
    /// <b>Measured on a live dark site before the fix:</b> <c>data-theme="dark"</c>, <c>data-bs-theme</c>
    /// absent, and <c>body</c> computed <c>rgb(255,255,255)</c> — a white page wearing dark <c>--db-*</c>
    /// cards, whose headings rendered near-black on dark and were unreadable. Setting the one attribute took
    /// <c>body</c> to <c>rgb(33,37,41)</c> with <c>rgb(222,226,230)</c> text and the page became coherent.
    /// </para>
    /// <para>
    /// This is a SOURCE gate rather than a rendered-DOM one deliberately: the defect is a line going missing
    /// from a hand-written bootstrap script, and the failure mode is silent — it renders, it just renders
    /// wrong, and only under a theme that is not the default. Themes that do not load Bootstrap are
    /// unaffected, since the attribute is inert without it.
    /// </para>
    /// </remarks>
    public class ThemeColourModeSyncTests
    {
        [Fact]
        public void The_theme_apply_function_sets_the_Bootstrap_colour_mode_alongside_its_own()
        {
            var app = ReadAppRazor();
            var apply = ExtractApplyFunction(app);

            Assert.Contains("data-theme", apply);
            Assert.True(
                Regex.IsMatch(apply, @"setAttribute\(\s*'data-bs-theme'\s*,"),
                "stommeTheme.apply() no longer sets 'data-bs-theme'. A Bootstrap-based theme (BlazorTheme " +
                "loads Bootstrap 5.3) then keeps its light chrome while the --db-* layer goes dark, which " +
                "measured as a white page carrying dark cards with unreadable near-black headings.");
        }

        [Fact]
        public void The_Bootstrap_colour_mode_is_set_from_the_RESOLVED_mode_not_the_raw_one()
        {
            // 'system' must be resolved to dark/light first; handing Bootstrap the literal 'system' would
            // leave it on its default light theme while --db-* correctly followed the OS preference.
            var apply = ExtractApplyFunction(ReadAppRazor());

            Assert.True(
                Regex.IsMatch(apply, @"setAttribute\(\s*'data-bs-theme'\s*,\s*resolved\s*\)"),
                "'data-bs-theme' must be set from `resolved`, not from `mode` — 'system' is not a Bootstrap " +
                "colour mode and would silently leave Bootstrap light.");
        }

        [Fact]
        public void The_theme_apply_function_hands_the_mode_to_FluentUI_5()
        {
            // FluentUI 5 has no <fluent-design-theme> element to set; its own script keeps the mode. Without this
            // line the --db-* surfaces turn dark while every FluentUI control stays light - the same split measured
            // for Bootstrap.
            var apply = ExtractApplyFunction(ReadAppRazor());
            Assert.Matches(@"setThemeMode\(\s*mode\s*\)", apply);
            Assert.DoesNotMatch(@"querySelector\(\s*'fluent-design-theme'", ReadAppRazor());
        }

        [Fact]
        public void With_no_stored_choice_the_mode_is_the_VISITORS_system_preference_not_dark()
        {
            // The bootstrap fell back to a hardcoded 'dark' - a light-preferring visitor got a dark site - and the
            // FluentUI theme state defaulted to Dark the same way. Both now start at "system". The visitor's
            // choice is read by stored(), which answers "system" when nothing is stored AND when the browser refuses storage
            // (the bootstrap used to give such a browser no mode at all); a theme's fixed mode is the only thing above it.
            var app = ReadAppRazor();
            var start = app.IndexOf("stored: function", StringComparison.Ordinal);
            Assert.True(start >= 0, "Could not find stommeTheme.stored in App.razor - the theme bootstrap moved.");
            var stored = app.Substring(start, app.IndexOf("seedFluent:", start, StringComparison.Ordinal) - start);

            Assert.Matches(@"if \(!stored\) \{ return 'system'; \}", stored);
            Assert.Matches(@"\.mode \|\| 'system'", stored);
            Assert.Matches(@"catch \(e\) \{ return 'system'; \}", stored);
            Assert.DoesNotMatch(@"'dark'", stored);
            Assert.Matches(@"window\.stommeTheme\.apply\(window\.stommeTheme\.stored\(\)\);", app);
            Assert.Matches(@"mode = window\.stommeTheme\.fixed\(\) \|\| \(mode \|\| 'system'\)", ExtractApplyFunction(app));

            Assert.Equal(Microsoft.FluentUI.AspNetCore.Components.ThemeMode.System, new Stomme.Services.ThemeState().Mode);
        }

        [Fact]
        public void The_browsers_own_widgets_follow_the_RESOLVED_mode_and_an_OS_change_repaints_a_system_page()
        {
            var app = ReadAppRazor();

            // A forced mode must not leave the scrollbars and form controls on the OS's scheme.
            Assert.Matches(@"style\.colorScheme\s*=\s*resolved", ExtractApplyFunction(app));

            // While the mode is "system", an OS switch repaints live - and ONLY then: a visitor's explicit choice stands.
            Assert.Matches(@"addEventListener\('change', follow\)", app);
            Assert.Matches(@"getAttribute\('data-theme-mode'\) === 'system'", app);
        }

        private static string ExtractApplyFunction(string appRazor)
        {
            var start = appRazor.IndexOf("apply: function", StringComparison.Ordinal);
            Assert.True(start >= 0, "Could not find stommeTheme.apply in App.razor — the theme bootstrap moved.");
            var end = appRazor.IndexOf("toggle:", start, StringComparison.Ordinal);
            Assert.True(end > start, "Could not delimit stommeTheme.apply — the theme bootstrap moved.");
            return appRazor.Substring(start, end - start);
        }

        private static string ReadAppRazor()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Stomme.slnx")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);

            var path = Path.Combine(dir!.FullName, "src", "Stomme.Server", "Components", "App.razor");
            Assert.True(File.Exists(path), $"App.razor not found at {path}");
            return File.ReadAllText(path);
        }
    }
}
