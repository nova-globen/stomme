#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// An application registered with explicit consent asks the signed-in user first (BL-251, BL-239's D#4: the consent type
    /// was stored and never enforced). Driven over HTTP on an installed SQLite site: the authorize endpoint shows a consent
    /// page instead of a code; <c>prompt=none</c> is <c>consent_required</c>; Deny is <c>access_denied</c>; a post without the
    /// antiforgery token is refused; Allow issues a code, and the next sign-in issues one without asking. An implicit
    /// application never asks.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class OpenIddictExplicitConsentTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Asking = "asking-app";
        private const string Trusted = "trusted-app";
        private const string Redirect = "http://127.0.0.1:7272/callback";

        [Fact]
        public async Task An_explicit_consent_application_asks_once_and_honours_the_answer()
        {
            var settings = new JsonObject
            {
                ["OpenIddict"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["Applications"] = new JsonArray(Client(Asking, "explicit", "Asking App"), Client(Trusted, "implicit", "Trusted App")),
                },
            };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);

                // The implicit application is never asked.
                var trusted = await SendAsync(client, cookies, HttpMethod.Get, AuthorizeUrl(Trusted));
                Assert.Equal(HttpStatusCode.Redirect, trusted.StatusCode);
                Assert.Contains("code=", trusted.Headers.Location!.ToString());

                // prompt=none without an answer yet: consent_required.
                var silent = await SendAsync(client, cookies, HttpMethod.Get, AuthorizeUrl(Asking, prompt: "none"));
                Assert.Contains("error=consent_required", silent.Headers.Location?.ToString() ?? await silent.Content.ReadAsStringAsync());

                // The consent page: who asks and for what, with Allow and Deny.
                var page = await SendAsync(client, cookies, HttpMethod.Get, AuthorizeUrl(Asking));
                var html = await page.Content.ReadAsStringAsync();
                output.WriteLine($"consent page: {(int)page.StatusCode}, {html.Length} chars");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Contains("Asking App", html);
                Assert.Contains("value=\"accept\"", html);
                Assert.Contains("value=\"deny\"", html);
                var fields = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
                    .Select(match => new KeyValuePair<string, string>(WebUtility.HtmlDecode(match.Groups[1].Value), WebUtility.HtmlDecode(match.Groups[2].Value))).ToList();
                Assert.Contains(fields, field => field.Key == "client_id" && field.Value == Asking);

                // Without the antiforgery token the answer is refused.
                var forged = await SendAsync(client, cookies, HttpMethod.Post, "/connect/authorize",
                    new FormUrlEncodedContent(fields.Where(field => field.Key != "__RequestVerificationToken").Append(new("stomme_consent", "accept"))));
                Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

                // Deny: access_denied, and still asked next time.
                var denied = await SendAsync(client, cookies, HttpMethod.Post, "/connect/authorize", new FormUrlEncodedContent(fields.Append(new("stomme_consent", "deny"))));
                output.WriteLine($"deny -> {(int)denied.StatusCode} {denied.Headers.Location}");
                Assert.Contains("error=access_denied", denied.Headers.Location?.ToString() ?? "");

                // Allow: a code, and the next sign-in issues one without asking.
                var allowed = await SendAsync(client, cookies, HttpMethod.Post, "/connect/authorize", new FormUrlEncodedContent(fields.Append(new("stomme_consent", "accept"))));
                output.WriteLine($"allow -> {(int)allowed.StatusCode} {allowed.Headers.Location}");
                Assert.StartsWith(Redirect, allowed.Headers.Location?.ToString() ?? "");
                Assert.Contains("code=", allowed.Headers.Location!.ToString());

                var again = await SendAsync(client, cookies, HttpMethod.Get, AuthorizeUrl(Asking, prompt: "none"));
                Assert.Contains("code=", again.Headers.Location?.ToString() ?? "");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static JsonObject Client(string clientId, string consent, string displayName) => new()
        {
            ["ClientId"] = clientId,
            ["DisplayName"] = displayName,
            ["Type"] = "public",
            ["ConsentType"] = consent,
            ["GrantTypes"] = new JsonArray("authorization_code"),
            ["Scopes"] = new JsonArray("openid", "profile"),
            ["RedirectUris"] = new JsonArray(Redirect),
        };

        private static string AuthorizeUrl(string clientId, string? prompt = null)
        {
            var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var query = new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["scope"] = "openid profile",
                ["redirect_uri"] = Redirect,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = "drive",
            };
            if (prompt is not null)
            {
                query["prompt"] = prompt;
            }

            return QueryHelpers.AddQueryString("/connect/authorize", query);
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpClient client, CookieContainer cookies, HttpMethod method, string path, HttpContent? content = null)
        {
            var uri = new Uri(new Uri("http://" + Host), path);
            var request = new HttpRequestMessage(method, uri) { Content = content };
            var header = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    cookies.SetCookies(uri, setCookie.Split(';')[0]);
                }
            }

            return response;
        }
    }
}
