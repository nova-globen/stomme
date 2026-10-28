using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using Stomme.Models;
using Stomme.Services;
using Stomme.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;
// `Database` alone would resolve to the Stomme.Database NAMESPACE from inside Stomme.Tests.
using DatabaseModel = Stomme.Models.Database;
using InstallerComponent = Stomme.Installer.Installer;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-14 (D86): pins the installer wizard's deliberate no-Router navigation model. The Installer runs
    /// WITHOUT an interactive &lt;Router&gt; — step changes set the internal <c>_step</c> field and update the
    /// URL cosmetically via <c>history.pushState</c> through JSInterop, because a
    /// <c>NavigationManager.NavigateTo</c> would REMOUNT the component and WIPE every collected wizard value
    /// (measured in D86: Back reset the DB choice and re-ran OnInitialized). Nothing else pins this; a green
    /// build cannot see the regression. These tests render the REAL Installer (with the REAL SqliteConfig DB
    /// control resolved reflectively from the ControlType string, exactly as the product does), drive the
    /// wizard Database → Account → Configure and back, and assert the three-part invariant:
    ///   (a) collected field state survives Back/Back (the D86 wipe regression),
    ///   (b) bUnit's navigation manager recorded ZERO navigations — the load-bearing invariant, and
    ///   (c) the history.pushState/replaceState JSInterop calls WERE observed (the URL mechanism really ran).
    /// Validated in both directions: swapping GoToStep's pushState for NavigationManager.NavigateTo makes
    /// Wizard_records_zero_navigations_and_uses_pushState fail (recorded navigations + missing pushState).
    /// </summary>
    public class InstallerWizardNavigationTests : BunitContext
    {
        private const string SqliteFileName = "stomme-bl14-wizard.db";
        private const string HostUsername = "host";
        private const string HostPassword = "S3cure!Passw0rd";
        private const string HostEmail = "host@example.com";
        private const string HostFullName = "Host User";

        private IRenderedComponent<InstallerComponent> RenderInstaller()
        {
            // Loose mode satisfies the wizard's only JS coupling (history.pushState/replaceState via
            // InvokeVoidAsync) while still RECORDING every invocation for assertion (c).
            JSInterop.Mode = JSRuntimeMode.Loose;

            Services.AddSingleton(new SiteState());
            Services.AddSingleton<IStringLocalizer<InstallerComponent>>(new EchoLocalizer<InstallerComponent>());
            Services.AddSingleton<IStringLocalizer<SharedResources>>(new EchoLocalizer<SharedResources>());
            // The Label control (LocalizableComponent -> ModuleBase) injects these; none is exercised beyond
            // construction in the pre-install wizard, but Blazor requires the registrations to exist.
            Services.AddSingleton<IStringLocalizerFactory>(new EchoLocalizerFactory());
            Services.AddSingleton<ILogService>(new ThrowingLogService());

            Services.AddSingleton<IDatabaseService>(new FakeDatabaseService());
            Services.AddSingleton<ISiteTemplateService>(new FakeSiteTemplateService());
            Services.AddSingleton<IInstallationService>(new FakeInstallationService());
            // Never invoked on the navigation path — throwing fakes make any accidental call loud.
            Services.AddSingleton<IUserService>(new ThrowingUserService());
            Services.AddSingleton<ISiteService>(new ThrowingSiteService());

            return Render<InstallerComponent>();
        }

        [Fact]
        public void Installer_starts_on_the_database_step_and_replaceStates_its_url()
        {
            var cut = RenderInstaller();

            // The first wizard pane is the database step, with the REAL SqliteConfig control rendered from
            // its assembly-qualified ControlType string (the same reflective path the product takes).
            Assert.Equal("DatabaseConfig", cut.Find("h2").TextContent.Trim());
            Assert.NotNull(cut.Find("#server")); // SqliteConfig's file-name input

            // First render stamps the deep-linkable step URL via history.replaceState (not pushState — no
            // spurious history entry), and no Blazor navigation happens.
            var replaceCalls = JSInterop.Invocations.Where(i => i.Identifier == "history.replaceState").ToList();
            Assert.Single(replaceCalls);
            Assert.Equal("/?step=database", replaceCalls[0].Arguments[2]);
            Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "history.pushState");

            var nav = Assert.IsAssignableFrom<BunitNavigationManager>(Services.GetRequiredService<NavigationManager>());
            Assert.Empty(nav.History);
        }

        [Fact]
        public void Wizard_preserves_collected_state_across_Back_Back()
        {
            var cut = RenderInstaller();

            // --- Database step: give the SQLite control a distinctive file name and advance. Next() reads the
            // live control (ReadLiveConnectionString) and caches the connection string in the Installer.
            cut.Find("#server").Change(SqliteFileName);
            ClickButton(cut, "Next");

            // --- Account step: fill every field the validation gate (IsAccountStepValid) checks.
            Assert.Equal("ApplicationAdmin", cut.Find("h2").TextContent.Trim());
            FillAccountStep(cut);
            ClickButton(cut, "Next");

            // --- Configure step (no configurable modules -> the friendly no-op pane).
            Assert.Equal("ConfigureModules", cut.Find("h2").TextContent.Trim());
            Assert.Contains("NoModulesToConfigure", cut.Markup);

            // --- Back to Account: every collected account value must still be present (the D86 wipe
            // regression — a NavigateTo-based step change would have remounted and reset all of these).
            ClickButton(cut, "Back");
            Assert.Equal("ApplicationAdmin", cut.Find("h2").TextContent.Trim());
            Assert.Equal(HostUsername, cut.Find("#username").GetAttribute("value"));
            Assert.Equal(HostPassword, cut.Find("#password").GetAttribute("value"));
            Assert.Equal(HostPassword, cut.Find("#confirm").GetAttribute("value"));
            Assert.Equal(HostEmail, FindEmailInput(cut).GetAttribute("value"));
            Assert.Equal(HostFullName, cut.Find("input[maxlength='50']").GetAttribute("value"));
            Assert.Equal(Constants.DefaultSiteTemplate, cut.Find("#template").GetAttribute("value"));

            // --- Back to Database: the DB choice survives, and the cached connection string + account state
            // keep every breadcrumb step reachable (CanNavigateTo still true across the board). Honest limit:
            // the SqliteConfig control itself is only rendered on this step, so its own input is recreated
            // fresh here by design — the Installer's cached _resolvedConnectionString is what persists, which
            // is exactly what the enabled breadcrumb proves.
            ClickButton(cut, "Back");
            Assert.Equal("DatabaseConfig", cut.Find("h2").TextContent.Trim());
            Assert.Equal("SQLite", cut.Find("#databaseType").GetAttribute("value"));
            var breadcrumbButtons = cut.Find("[role=navigation]").QuerySelectorAll("button").ToList();
            Assert.Equal(4, breadcrumbButtons.Count);
            Assert.All(breadcrumbButtons, b => Assert.False(b.HasAttribute("disabled"),
                $"breadcrumb '{b.TextContent.Trim()}' should stay reachable — collected state was lost"));
        }

        [Fact]
        public void Wizard_records_zero_navigations_and_uses_pushState()
        {
            var cut = RenderInstaller();
            var nav = Assert.IsAssignableFrom<BunitNavigationManager>(Services.GetRequiredService<NavigationManager>());
            var initialUri = nav.Uri;

            // Drive the full flow: Database -> Account -> Configure -> Back -> Back.
            cut.Find("#server").Change(SqliteFileName);
            ClickButton(cut, "Next");
            FillAccountStep(cut);
            ClickButton(cut, "Next");
            ClickButton(cut, "Back");
            ClickButton(cut, "Back");

            // (b) THE load-bearing invariant: no step change may go through NavigationManager. A regression
            // that swaps GoToStep's pushState for NavigateTo records history entries here (and, in the real
            // product, remounts the Installer and wipes all collected state — measured in D86).
            Assert.Empty(nav.History);
            Assert.Equal(initialUri, nav.Uri);

            // (c) The cosmetic URL mechanism really ran: one history.pushState per step change, in order.
            var pushCalls = JSInterop.Invocations
                .Where(i => i.Identifier == "history.pushState")
                .Select(i => i.Arguments[2] as string)
                .ToList();
            Assert.Equal(new[] { "/?step=account", "/?step=configure", "/?step=account", "/?step=database" }, pushCalls);
        }

        // ---- drive helpers -------------------------------------------------------------------------

        private static void ClickButton(IRenderedComponent<InstallerComponent> cut, string text)
        {
            var button = cut.FindAll("button").Single(b => b.TextContent.Trim() == text);
            button.Click();
        }

        private static void FillAccountStep(IRenderedComponent<InstallerComponent> cut)
        {
            cut.Find("#username").Change(HostUsername);
            cut.Find("#password").Change(HostPassword);
            cut.Find("#confirm").Change(HostPassword);
            FindEmailInput(cut).Change(HostEmail);
            cut.Find("input[maxlength='50']").Change(HostFullName); // full name (the sole 50-char field)
        }

        // The email input carries the id its label names since BL-22's label sweep (2026-10-03); before it, this found the
        // one maxlength-256 account input without an id.
        private static AngleSharp.Dom.IElement FindEmailInput(IRenderedComponent<InstallerComponent> cut)
        {
            return cut.Find("#email");
        }

        // ---- minimal fakes -------------------------------------------------------------------------

        private sealed class EchoLocalizer<T> : IStringLocalizer<T>
        {
            public LocalizedString this[string name] => new(name, name);
            public LocalizedString this[string name, params object[] arguments] => new(name, name);
            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
        }

        private sealed class EchoLocalizerFactory : IStringLocalizerFactory
        {
            public IStringLocalizer Create(Type resourceSource) => new EchoLocalizer<object>();
            public IStringLocalizer Create(string baseName, string location) => new EchoLocalizer<object>();
        }

        private sealed class FakeDatabaseService : IDatabaseService
        {
            public Task<List<DatabaseModel>> GetDatabasesAsync() => Task.FromResult(new List<DatabaseModel>
            {
                new()
                {
                    // The REAL SQLite installer-control pairing from appsettings.json AvailableDatabases —
                    // Type.GetType over this ControlType is the same reflective load the product performs.
                    Name = "SQLite",
                    ControlType = "Stomme.Installer.Controls.SqliteConfig, Stomme.Client",
                    DBType = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    IsDefault = true,
                },
            });
        }

        private sealed class FakeSiteTemplateService : ISiteTemplateService
        {
            public Task<List<SiteTemplate>> GetSiteTemplatesAsync() => Task.FromResult(new List<SiteTemplate>
            {
                new() { Name = "Default Site Template", TypeName = Constants.DefaultSiteTemplate },
            });
        }

        private sealed class FakeInstallationService : IInstallationService
        {
            public Task<List<ConfigurableModule>> GetConfigurableModulesAsync() => Task.FromResult(new List<ConfigurableModule>());
            public Task<Installation> IsInstalled() => throw NotOnNavigationPath();
            public Task<Installation> Install(InstallConfig config) => throw NotOnNavigationPath();
            public Task<Installation> Upgrade(bool backup) => throw NotOnNavigationPath();
            public Task RestartAsync() => throw NotOnNavigationPath();
        }

        private static NotSupportedException NotOnNavigationPath() =>
            new("Not expected on the wizard navigation path — a call here means the flow under test changed.");

        private sealed class ThrowingLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => throw NotOnNavigationPath();
            public Task<Log> GetLogAsync(int logId) => throw NotOnNavigationPath();
            public Task DeleteLogsAsync(int siteId) => throw NotOnNavigationPath();
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotOnNavigationPath();
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotOnNavigationPath();
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => throw NotOnNavigationPath();
        }

        private sealed class ThrowingSiteService : ISiteService
        {
            public Task<List<Site>> GetSitesAsync() => throw NotOnNavigationPath();
            public Task<Site> GetSiteAsync(int siteId) => throw NotOnNavigationPath();
            public Task<Site> AddSiteAsync(Site site) => throw NotOnNavigationPath();
            public Task<Site> UpdateSiteAsync(Site site) => throw NotOnNavigationPath();
            public Task DeleteSiteAsync(int siteId) => throw NotOnNavigationPath();
            public Task<List<Module>> GetModulesAsync(int siteId, int pageId) => throw NotOnNavigationPath();
            public void SetAlias(Alias alias) => throw NotOnNavigationPath();
        }

        private sealed class ThrowingUserService : IUserService
        {
            public Task<User> GetUserAsync(int userId, int siteId) => throw NotOnNavigationPath();
            public Task<User> GetUserAsync(string username, int siteId) => throw NotOnNavigationPath();
            public Task<User> GetUserAsync(string username, string email, int siteId) => throw NotOnNavigationPath();
            public Task<User> AddUserAsync(User user) => throw NotOnNavigationPath();
            public Task<User> UpdateUserAsync(User user) => throw NotOnNavigationPath();
            public Task DeleteUserAsync(int userId, int siteId) => throw NotOnNavigationPath();
            public Task DeleteMyAccountAsync() => throw NotOnNavigationPath();
            public Task<User> LoginUserAsync(User user, bool setCookie, bool isPersistent) => throw NotOnNavigationPath();
            public Task LogoutUserAsync(User user) => throw NotOnNavigationPath();
            public Task LogoutUserEverywhereAsync(User user) => throw NotOnNavigationPath();
            public Task<User> VerifyEmailAsync(User user, string token) => throw NotOnNavigationPath();
            public Task<bool> ForgotPasswordAsync(string username) => throw NotOnNavigationPath();
            public Task<bool> ForgotUsernameAsync(string email) => throw NotOnNavigationPath();
            public Task<User> ResetPasswordAsync(User user, string token) => throw NotOnNavigationPath();
            public Task<User> VerifyTwoFactorAsync(User user, string token) => throw NotOnNavigationPath();
            public Task<UserValidateResult> ValidateUserAsync(string username, string email, string password) => throw NotOnNavigationPath();
            public Task<bool> ValidatePasswordAsync(string password) => throw NotOnNavigationPath();
            public Task<string> GetTokenAsync() => throw NotOnNavigationPath();
            public Task<string> GetPersonalAccessTokenAsync() => throw NotOnNavigationPath();
            public Task<string> GetPasswordRequirementsAsync(int siteId) => throw NotOnNavigationPath();
            public Task<List<UserPasskey>> GetPasskeysAsync(int userId) => throw NotOnNavigationPath();
            public Task<UserPasskey> UpdatePasskeyAsync(UserPasskey passkey) => throw NotOnNavigationPath();
            public Task DeletePasskeyAsync(int userId, byte[] credentialId) => throw NotOnNavigationPath();
            public Task<List<UserLogin>> GetLoginsAsync(int userId) => throw NotOnNavigationPath();
            public Task<User> AddLoginAsync(User user, string token, string type, string key, string name) => throw NotOnNavigationPath();
            public Task DeleteLoginAsync(int userId, string provider, string key) => throw NotOnNavigationPath();
            public Task<bool> SendLoginLinkAsync(string email, string returnurl) => throw NotOnNavigationPath();
        }
    }
}
