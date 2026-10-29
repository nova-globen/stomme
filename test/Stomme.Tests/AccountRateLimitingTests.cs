#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The account endpoints are rate-limited per (site, client address), driven over HTTP on an installed host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A low limit is configured (through the override file, where an install keeps it) rather than sending ten wrong
    /// passwords: what is driven is that each endpoint is WIRED to its policy, that the Nth attempt is refused while
    /// ANOTHER client is not, and that the refusal says nothing but 429 and when to come back.
    /// </para>
    /// <para>
    /// The login FORM and <c>api/User/signin</c> share one budget - one policy, one partition - so an attacker cannot
    /// double the allowance by alternating between them.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class AccountRateLimitingTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public AccountRateLimitingTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_Nth_sign_in_attempt_is_refused_for_THAT_client_and_another_client_is_not()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: Limits(signIn: 3, email: 0));
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                using var attacker = GateDrive.NewClient(app, Host);

                // Three wrong passwords through the FORM are answered as ever (a redirect back)...
                var statuses = new List<HttpStatusCode>();
                for (var i = 0; i < 3; i++)
                {
                    statuses.Add((await LoginFormAsync(app, attacker, GateDrive.HostUsername, "wrong-" + i)).StatusCode);
                }

                Assert.All(statuses, status => Assert.NotEqual(HttpStatusCode.TooManyRequests, status));

                // ...the fourth is refused BY THE LIMITER: 429, when to retry, one plain sentence and nothing else.
                var refused = await LoginFormAsync(app, attacker, GateDrive.HostUsername, "wrong-3");
                var body = await refused.Content.ReadAsStringAsync();
                _output.WriteLine($"[refused] {(int)refused.StatusCode} Retry-After={refused.Headers.RetryAfter} '{body}'");
                Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
                Assert.NotNull(refused.Headers.RetryAfter);
                Assert.Equal("text/plain", refused.Content.Headers.ContentType?.MediaType);
                Assert.DoesNotContain(AccountRateLimiting.SignInPolicy, body);

                // The API sign-in is the SAME budget, not a second one to spend.
                var api = await GateDrive.SendJsonAsync(app, attacker, null, Host, null, HttpMethod.Post, "/api/User/signin",
                    new User { Username = GateDrive.HostUsername, Password = "wrong-api" }, _output, "api signin from the same client");
                Assert.Equal(HttpStatusCode.TooManyRequests, api.Status);
                Assert.Contains("Too many requests", api.Body);

                // Another client - another address - is untouched, and signs in.
                using var visitor = GateDrive.NewClient(app, Host);
                await GateDrive.LoginAsync(app, visitor, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Mail_sending_endpoints_are_limited_and_an_ADMIN_adding_users_is_not()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: Limits(signIn: 0, email: 2));
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                // Forgot-password: two requests, then refused; another client still gets through.
                using var anonymous = GateDrive.NewClient(app, Host);
                var forgot = new List<HttpStatusCode>();
                for (var i = 0; i < 3; i++)
                {
                    forgot.Add((await GateDrive.GetAsync(anonymous, null, Host, "/api/User/forgotpassword/nobody-" + i, _output, $"forgot {i + 1}")).Status);
                }

                Assert.Equal(HttpStatusCode.TooManyRequests, forgot[2]);
                Assert.DoesNotContain(HttpStatusCode.TooManyRequests, forgot.Take(2));
                using var other = GateDrive.NewClient(app, Host);
                Assert.NotEqual(HttpStatusCode.TooManyRequests, (await GateDrive.GetAsync(other, null, Host, "/api/User/forgotpassword/nobody", _output, "forgot, other client")).Status);

                // Anonymous registration shares the mail budget's size and is refused past it...
                var alias = await GateDrive.AliasAsync(app, Host);
                using var registrant = GateDrive.NewClient(app, Host);
                var registrations = new List<HttpStatusCode>();
                for (var i = 0; i < 3; i++)
                {
                    registrations.Add((await GateDrive.SendJsonAsync(app, registrant, null, Host, null, HttpMethod.Post, "/api/User",
                        new User { SiteId = alias.SiteId, Username = "bl121reg" + i, Password = "Bl121!Reg$2026", Email = $"bl121reg{i}@gate.test", DisplayName = "BL-121" },
                        _output, $"anonymous registration {i + 1}")).Status);
                }

                Assert.Equal(HttpStatusCode.TooManyRequests, registrations[2]);

                // ...while an ADMINISTRATOR adding users through the same action is not limited at all.
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var admin = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, admin, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                for (var i = 0; i < 4; i++)
                {
                    var added = await GateDrive.SendJsonAsync(app, admin, cookies, Host, hostId, HttpMethod.Post, "/api/User",
                        new User { SiteId = alias.SiteId, Username = "bl121admin" + i, Password = "Bl121!Adm$2026", Email = $"bl121admin{i}@gate.test", DisplayName = "BL-121" },
                        _output, $"admin adds user {i + 1}");
                    Assert.Equal(HttpStatusCode.OK, added.Status);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public void The_defaults_are_ten_a_minute_and_five_in_five_minutes_and_ZERO_means_off_not_refuse_all()
        {
            var none = new ConfigurationBuilder().Build();
            Assert.Equal((10, 60), AccountRateLimiting.Read(none, "SignIn", AccountRateLimiting.SignInDefault));
            Assert.Equal((5, 300), AccountRateLimiting.Read(none, "Email", AccountRateLimiting.EmailDefault));

            var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Account:SignIn:PermitLimit"] = "0",
                ["RateLimiting:Account:SignIn:WindowSeconds"] = "0",     // a zero WINDOW is not a thing: the default stands
                ["RateLimiting:Account:Email:PermitLimit"] = "-4",       // nonsense reads as the default, never as "off"
            }).Build();
            Assert.Equal((0, 60), AccountRateLimiting.Read(configured, "SignIn", AccountRateLimiting.SignInDefault));
            Assert.Equal((5, 300), AccountRateLimiting.Read(configured, "Email", AccountRateLimiting.EmailDefault));
        }

        private static async Task<HttpResponseMessage> LoginFormAsync(WebApplication app, HttpClient client, string username, string password)
        {
            var (token, antiforgeryCookie) = GateDrive.Antiforgery(app);
            var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = username,
                    ["password"] = password,
                    ["remember"] = "false",
                    ["returnurl"] = "/",
                    ["__RequestVerificationToken"] = token,
                }),
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
            return await client.SendAsync(request);
        }

        private static JsonObject Limits(int signIn, int email) => new()
        {
            ["RateLimiting"] = new JsonObject
            {
                ["Account"] = new JsonObject
                {
                    ["SignIn"] = new JsonObject { ["PermitLimit"] = signIn, ["WindowSeconds"] = 300 },
                    ["Email"] = new JsonObject { ["PermitLimit"] = email, ["WindowSeconds"] = 300 },
                },
            },
        };
    }
}
