using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The admin write paths an earlier role fix did not reach. <c>ProfileController</c> and <c>LanguageController</c>
    /// checked only <c>ModelState</c>, and neither model carries an annotation, so a profile item with no name or title and a
    /// language with no culture code could be written through the API although both admin forms mark the field required.
    /// </summary>
    /// <remarks>
    /// Driven over real HTTP as the HOST, the way <c>RoleWriteValidationTests</c> drives roles: every blank shape is refused
    /// with 400 and writes no row, on POST and on PUT; an ordinary value still saves (non-vacuity).
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class AdminWriteBlankNameTests
    {
        private const string Host = EntityPermissionWriteValidationTests.Host;

        private readonly ITestOutputHelper _output;

        public AdminWriteBlankNameTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_profile_item_without_a_name_or_a_title_is_refused_and_writes_nothing()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                Profile Item(string name, string title) => new()
                {
                    SiteId = siteId, Name = name, Title = title, Description = "bl143", Category = "BL143", ViewOrder = 0,
                    MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Validation = "", Rows = 1, Autocomplete = ""
                };

                var before = (await ProfilesAsync(app, siteId)).Count;
                var failures = new List<string>();
                foreach (var (label, name, title) in new[] { ("empty name", "", "Title"), ("whitespace name", "   ", "Title"), ("empty title", "Bl143Field", ""), ("whitespace title", "Bl143Field", "  ") })
                {
                    var sent = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/Profile", Item(name, title), _output, $"POST profile ({label})");
                    if (sent.Status != HttpStatusCode.BadRequest) failures.Add($"POST {label}: expected 400, got {(int)sent.Status}");
                    if ((await ProfilesAsync(app, siteId)).Count != before) failures.Add($"POST {label}: a profile row was written");
                }

                // Non-vacuity: an ordinary item saves - and then a PUT blanking it is refused and changes nothing.
                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/Profile", Item("Bl143Field", "BL-143 field"), _output, "POST profile (valid)");
                Assert.Equal(HttpStatusCode.OK, created.Status);
                var saved = (await ProfilesAsync(app, siteId)).Single(profile => profile.Name == "Bl143Field");

                foreach (var (label, name, title) in new[] { ("empty name", "", "BL-143 field"), ("whitespace title", "Bl143Field", " ") })
                {
                    var blanked = Item(name, title);
                    blanked.ProfileId = saved.ProfileId;
                    var sent = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Put, $"/api/Profile/{saved.ProfileId}", blanked, _output, $"PUT profile ({label})");
                    if (sent.Status != HttpStatusCode.BadRequest) failures.Add($"PUT {label}: expected 400, got {(int)sent.Status}");
                }

                var after = (await ProfilesAsync(app, siteId)).Single(profile => profile.ProfileId == saved.ProfileId);
                if (after.Name != "Bl143Field" || after.Title != "BL-143 field") failures.Add($"PUT: the row changed to '{after.Name}' / '{after.Title}'");

                Assert.True(failures.Count == 0, string.Join("; ", failures));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_language_without_a_culture_code_is_refused_and_writes_nothing()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var before = (await LanguagesAsync(app, siteId)).Count;
                var failures = new List<string>();

                // "-" is the Add form's "choose one" placeholder: it is what a client that skipped the form's own check sends.
                foreach (var (label, code) in new[] { ("empty", ""), ("whitespace", "  "), ("placeholder", "-") })
                {
                    var sent = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/Language",
                        new Language { SiteId = siteId, Code = code, IsDefault = false }, _output, $"POST language ({label} code)");
                    if (sent.Status != HttpStatusCode.BadRequest) failures.Add($"POST {label} code: expected 400, got {(int)sent.Status}");
                    if ((await LanguagesAsync(app, siteId)).Count != before) failures.Add($"POST {label} code: a language row was written");
                }

                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/Language",
                    new Language { SiteId = siteId, Code = "sv-SE", IsDefault = false }, _output, "POST language (valid)");
                Assert.Equal(HttpStatusCode.OK, created.Status);
                var saved = (await LanguagesAsync(app, siteId)).Single(language => language.Code == "sv-SE");

                var blanked = new Language { LanguageId = saved.LanguageId, SiteId = siteId, Code = " ", IsDefault = false };
                var put = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Put, "/api/Language", blanked, _output, "PUT language (blank code)");
                if (put.Status != HttpStatusCode.BadRequest) failures.Add($"PUT blank code: expected 400, got {(int)put.Status}");
                if ((await LanguagesAsync(app, siteId)).Single(language => language.LanguageId == saved.LanguageId).Code != "sv-SE") failures.Add("PUT blank code: the row changed");

                Assert.True(failures.Count == 0, string.Join("; ", failures));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_user_with_a_blank_username_is_NOT_created_ASP_NET_Identity_refuses_it()
        {
            // The third suspect in the list. Measured rather than fixed: the user path is not the same shape as that role fix - it runs through
            // ASP.NET Identity's UserManager, whose UserValidator refuses a blank user name. This pins that it stays so.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var before = await UserCountAsync(app);
                foreach (var (label, username) in new[] { ("empty", ""), ("whitespace", "   ") })
                {
                    var sent = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/User",
                        new User { SiteId = siteId, Username = username, Password = "Bl143!Blank$2026", Email = $"bl143-{label}@gate.test", DisplayName = "BL-143" },
                        _output, $"POST user ({label} username)");
                    _output.WriteLine($"[POST user ({label})] -> {(int)sent.Status} {GateDrive.Truncate(sent.Body)}");
                    Assert.Equal(before, await UserCountAsync(app));
                }

                // Non-vacuity: an ordinary user IS created through the same call.
                await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/User",
                    new User { SiteId = siteId, Username = "bl143user", Password = "Bl143!Blank$2026", Email = "bl143user@gate.test", DisplayName = "BL-143" },
                    _output, "POST user (valid)");
                Assert.Equal(before + 1, await UserCountAsync(app));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<int> UserCountAsync(Microsoft.AspNetCore.Builder.WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IUserRepository>().GetUsersAsync()).Count());

        private static Task<List<Profile>> ProfilesAsync(Microsoft.AspNetCore.Builder.WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IProfileRepository>().GetProfilesAsync(siteId)).ToList());

        private static Task<List<Language>> LanguagesAsync(Microsoft.AspNetCore.Builder.WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<ILanguageRepository>().GetLanguagesAsync(siteId)).ToList());
    }
}
