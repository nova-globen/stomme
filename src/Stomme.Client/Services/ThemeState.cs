using Microsoft.FluentUI.AspNetCore.Components;

namespace Stomme.Services
{
    /// <summary>
    /// Holds the session's colour mode and cycles System -> Light -> Dark. The mode a visitor chooses lives
    /// client-side under "stomme-theme"; App.razor's <c>stommeTheme.apply()</c> applies it to the <c>--db-*</c>
    /// surfaces and to FluentUI 5 together (FluentUI 5 has no FluentDesignTheme).
    /// </summary>
    public sealed class ThemeState
    {
        /// <summary>
        /// System until the visitor chooses: a hardcoded Dark handed a light-preferring visitor a dark site, and
        /// App.razor's bootstrap made the same choice before first paint.
        /// </summary>
        public ThemeMode Mode { get; set; } = ThemeMode.System;

        // The signature Stomme accent (amber) that seeds the FluentUI brand accent so Fluent controls
        // and the --db-* surfaces read as one system.
        public string Accent { get; set; } = "#FFC418";

        public ThemeMode Cycle()
        {
            Mode = Mode switch
            {
                ThemeMode.System => ThemeMode.Light,
                ThemeMode.Light => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
            return Mode;
        }
    }
}
