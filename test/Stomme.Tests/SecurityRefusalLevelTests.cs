#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Enums;
using Stomme.Infrastructure.Security;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using LogLevel = Stomme.Shared.LogLevel;

namespace Stomme.Tests
{
    /// <summary>
    /// The owner's decision of 2026-10-10: a request refused to an ANONYMOUS caller is logged as a Warning - in the site's
    /// log, never mailed to its hosts - and one refused to a SIGNED-IN user stays an Error, mailed. Found on UAT the day both
    /// sites were wired to the mail relay: every anonymous probe of a key-gated API mailed the host.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SecurityRefusalLevelTests(ITestOutputHelper output)
    {
        private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

        private static ClaimsPrincipal SignedIn() => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "ada") }, "test"));

        [Fact]
        public void A_refusal_is_a_warning_for_an_anonymous_caller_and_an_error_for_a_signed_in_one()
        {
            Assert.Equal((LogLevel.Warning, LogLevel.Warning, LogLevel.Error), (SecurityRefusals.Level(null), SecurityRefusals.Level(Anonymous()), SecurityRefusals.Level(SignedIn())));

            // The inherited refusals say "Unauthorized ... Attempt"; nothing else changes level.
            const string refusal = "Unauthorized User Get Attempt {UserId} {SiteId}";
            Assert.Equal(LogLevel.Warning, SecurityRefusals.LevelFor(LogLevel.Error, LogFunction.Security, refusal, Anonymous()));
            Assert.Equal(LogLevel.Error, SecurityRefusals.LevelFor(LogLevel.Error, LogFunction.Security, refusal, SignedIn()));
            Assert.Equal(LogLevel.Error, SecurityRefusals.LevelFor(LogLevel.Error, LogFunction.Security, "Login Link Notification Failed For {Email}", Anonymous()));
            Assert.Equal(LogLevel.Error, SecurityRefusals.LevelFor(LogLevel.Error, LogFunction.Read, refusal, Anonymous()));
            Assert.Equal(LogLevel.Information, SecurityRefusals.LevelFor(LogLevel.Information, LogFunction.Security, refusal, Anonymous()));
            // The settings controller's refusals, under their own functions.
            const string settings = "User Not Authorized To Access Settings For EntityName {EntityName} And EntityId {EntityId}";
            Assert.Equal((LogLevel.Warning, LogLevel.Error), (SecurityRefusals.LevelFor(LogLevel.Error, LogFunction.Read, settings, Anonymous()), SecurityRefusals.LevelFor(LogLevel.Error, LogFunction.Read, settings, SignedIn())));
        }

        // On an installed host: an anonymous caller refused by a permission key and by an inherited check is two Warnings and no
        // mail; a signed-in user refused by the key is an Error and one mail.
        [Fact]
        public async Task An_anonymous_refusal_is_logged_as_a_warning_and_not_mailed_and_a_signed_in_one_is_an_error_mailed()
        {
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var siteId = (await GateDrive.AliasAsync(app, "localhost")).SiteId;
                await GateDrive.AddUserAsync(app, "localhost", GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var anonymous = GateDrive.NewClient(app, "localhost");
                Assert.True((await GateDrive.GetAsync(anonymous, null, "localhost", "/api/Pipelines", output, "anonymous: pipelines")).Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(anonymous, null, "localhost", "/api/Setting?entityName=User&entityId=1", output, "anonymous: a user's settings")).Status);
                using var plain = GateDrive.NewClient(app, "localhost");
                var cookies = await GateDrive.LoginAsync(app, plain, "localhost", GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plain, cookies, "localhost", "/api/Pipelines", output, "a plain user: pipelines")).Status);

                string Of(Stomme.Models.Log log) => (log.UserId > 0 ? "user" : "anonymous") + " " + log.Level + " " + (log.Message.Contains("User Not Authorized To Access Settings", StringComparison.Ordinal) ? "inherited" : "key");
                string[] logged = Array.Empty<string>();
                var mailed = 0;
                // Until the mail is queued too: it can land after its log row (measured - the gate once read 0 mails beside 3 rows).
                for (var attempt = 0; attempt < 100 && (logged.Length < 3 || mailed < 1); attempt++)
                {
                    await Task.Delay(100);
                    (logged, mailed) = await GateDrive.InTenantAsync(app, "localhost", async (sp, alias) =>
                    {
                        var rows = (await sp.GetRequiredService<ILogRepository>().GetLogsAsync(siteId, null, null, 200))
                            .Where(log => log.Message.Contains("pipelines:view", StringComparison.Ordinal) || log.Message.Contains("User Not Authorized To Access Settings", StringComparison.Ordinal))
                            .Select(Of).OrderBy(line => line, StringComparer.Ordinal).ToArray();
                        var queued = (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(siteId, -1, -1))
                            .Count(note => note.Body.Contains("pipelines:view", StringComparison.Ordinal) || note.Body.Contains("User Not Authorized To Access Settings", StringComparison.Ordinal));
                        return (rows, queued);
                    });
                }

                Assert.Equal(new[] { "anonymous Warning inherited", "anonymous Warning key", "user Error key" }, logged);
                Assert.Equal(1, mailed); // the signed-in user's refusal only
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
