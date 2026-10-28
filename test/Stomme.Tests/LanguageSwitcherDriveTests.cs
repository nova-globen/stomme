#nullable enable
using System;
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
    /// BL-251 - the language switcher changes the language. It writes the visitor's choice into the culture cookie, and every
    /// server render rewrote that cookie to the profile's language or the site's default, so the next page came back in the
    /// old language (found driving the app guidance's UI wave). Driven: on a site offering English and French, a page request
    /// carrying the switcher's French cookie keeps it (no rewrite to English) and the page says it is French; a cookie naming
    /// a language the site does not offer is still corrected.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class LanguageSwitcherDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string CookieName = ".AspNetCore.Culture";

        [Fact]
        public async Task The_language_the_visitor_chose_is_kept_and_one_the_site_does_not_offer_is_corrected()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var languages = sp.GetRequiredService<ILanguageRepository>();
                    await languages.AddLanguageAsync(new Language { SiteId = alias.SiteId, Name = "Français", Code = "fr", IsDefault = false });
                    return 0;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }

            app = await GateDrive.InstallAsync(scratch, output); // a fresh host, so the site's languages are read anew
            try
            {
                using var client = GateDrive.NewClient(app, Host);

                var french = await PageAsync(client, "c=en|uic=fr", "the switcher's choice: French");
                Assert.Equal(HttpStatusCode.OK, french.Status);
                Assert.DoesNotContain(french.SetCookies, cookie => cookie.StartsWith(CookieName + "=", StringComparison.Ordinal));
                Assert.Contains("<html lang=\"fr\"", french.Body);

                var unknown = await PageAsync(client, "c=en|uic=de", "a language the site does not offer");
                var rewritten = Assert.Single(unknown.SetCookies, cookie => cookie.StartsWith(CookieName + "=", StringComparison.Ordinal));
                Assert.Contains("uic%3Den", rewritten);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<(HttpStatusCode Status, string Body, string[] SetCookies)> PageAsync(HttpClient client, string cookieValue, string label)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://" + Host + "/"));
            request.Headers.TryAddWithoutValidation("Cookie", CookieName + "=" + Uri.EscapeDataString(cookieValue));
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : Array.Empty<string>();
            output.WriteLine($"[{label}] {(int)response.StatusCode}; Set-Cookie: {string.Join(" || ", setCookies.Select(c => c.Split(';')[0]))}");
            return (response.StatusCode, body, setCookies);
        }
    }
}
