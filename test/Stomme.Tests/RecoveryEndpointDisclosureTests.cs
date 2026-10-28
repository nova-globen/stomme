#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The anonymous recovery endpoints must not say whether an account exists, and must never put a reset token
    /// in the event log (BL-149).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both were read out of the code and are DRIVEN here over real HTTP.</b>
    /// <c>GET api/User/forgotpassword/{username}</c> and <c>GET api/User/forgotusername/{email}</c> carry no
    /// <c>[Authorize]</c> — neither the controller nor the methods — and returned the manager's raw <c>bool</c>:
    /// <c>true</c> when the lookup found somebody, <c>false</c> when it did not. That is an account-existence
    /// oracle available to anyone who can reach the site, and since BL-90 the only thing standing in front of it
    /// is a per-site switch that turns recovery off altogether.
    /// </para>
    /// <para>
    /// <b>It compounds.</b> BL-121 records that nothing rate-limits these endpoints, so an oracle plus unlimited
    /// attempts is enumeration at scale rather than a single question. Rate limiting is NOT fixed here — this row
    /// closes the oracle, and the two rows are only dangerous together.
    /// </para>
    /// <para>
    /// <b>The token half is a disclosure to administrators.</b> <c>UserManager.ResetPassword</c> logged
    /// <c>"Password Reset Failed For {Username} And Token {Token}"</c>, putting a live password-reset token into
    /// the site Log table that every administrator can read. The assertion below checks EVERY string column of
    /// the row, not just the message: the token also rides the query string, and <c>LogManager</c> copies the
    /// request URL into <c>Log.Url</c> for every entry raised during that request — the same second door BL-112's
    /// fix left open for setting values (BL-148).
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class RecoveryEndpointDisclosureTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public RecoveryEndpointDisclosureTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_known_and_an_unknown_username_get_the_same_answer()
        {
            var store = LiftedRequiresGateTests.ResolveStore(SqliteGateStore.StoreName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                await GateDrive.AddUserAsync(app, Host, "recovery.known", "R3covery!Known$2026", "known@recovery.test");

                using var client = GateDrive.NewClient(app, Host);
                var known = await client.GetAsync("/api/User/forgotpassword/recovery.known");
                var unknown = await client.GetAsync("/api/User/forgotpassword/recovery.nobody");

                var knownBody = await known.Content.ReadAsStringAsync();
                var unknownBody = await unknown.Content.ReadAsStringAsync();

                _output.WriteLine($"[known  ] {(int)known.StatusCode} '{knownBody}'");
                _output.WriteLine($"[unknown] {(int)unknown.StatusCode} '{unknownBody}'");

                Assert.Equal(known.StatusCode, unknown.StatusCode);
                Assert.Equal(knownBody, unknownBody);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_known_and_an_unknown_email_get_the_same_answer()
        {
            var store = LiftedRequiresGateTests.ResolveStore(SqliteGateStore.StoreName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                await GateDrive.AddUserAsync(app, Host, "recovery.mail", "R3covery!Mail$2026", "mail@recovery.test");

                using var client = GateDrive.NewClient(app, Host);
                var known = await client.GetAsync("/api/User/forgotusername/mail@recovery.test");
                var unknown = await client.GetAsync("/api/User/forgotusername/nobody@recovery.test");

                var knownBody = await known.Content.ReadAsStringAsync();
                var unknownBody = await unknown.Content.ReadAsStringAsync();

                _output.WriteLine($"[known  ] {(int)known.StatusCode} '{knownBody}'");
                _output.WriteLine($"[unknown] {(int)unknown.StatusCode} '{unknownBody}'");

                Assert.Equal(known.StatusCode, unknown.StatusCode);
                Assert.Equal(knownBody, unknownBody);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_reset_token_never_reaches_the_event_log()
        {
            var store = LiftedRequiresGateTests.ResolveStore(SqliteGateStore.StoreName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                // A token shaped like nothing else in the log, so finding it is unambiguous.
                var token = "BL149-TOKEN-" + Guid.NewGuid().ToString("N");

                using var client = GateDrive.NewClient(app, Host);
                // A user that actually EXISTS, so the request reaches UserManager.ResetPassword rather than
                // bouncing earlier - the token only gets logged on the path that runs.
                await GateDrive.AddUserAsync(app, Host, "recovery.reset", "R3covery!Reset$2026", "reset@recovery.test");

                // Through the harness's antiforgery-paired sender: a bare POST is refused with an EMPTY 400 by the
                // antiforgery filter, which would make this test pass without the logging path ever running.
                var (status, responseBody) = await GateDrive.SendJsonAsync(
                    app, client, cookies: null, Host, actorUserId: null,
                    HttpMethod.Post, $"/api/User/reset?token={WebUtility.UrlEncode(token)}",
                    new User
                    {
                        Username = "recovery.reset",
                        Password = "N3wPassw0rd!$2026",
                        DisplayName = "Recovery Reset",
                        Email = "reset@recovery.test",
                        SiteId = siteId,
                        TimeZoneId = "UTC",
                        CultureCode = "en-US",
                    },
                    _output, "reset");

                _output.WriteLine($"[reset] {(int)status} {Trim(responseBody)}");

                Assert.True(status != HttpStatusCode.BadRequest,
                    "the reset request never reached UserManager.ResetPassword, so this test would pass without " +
                    "exercising the logging path at all: " + Trim(responseBody));

                // The D34 drain writes from a background thread, so the row is not there the instant the response
                // returns. Poll for it - and make its arrival a POSITIVE CONTROL: without proof that this
                // request's own entry reached the table, "the token is absent" is a claim about an empty table.
                // The OTHER branch. The reset above names a user that exists, so it fails inside Identity and
                // takes the "result.Succeeded == false" path. The branch BL-149 actually named - the one that
                // spelled the token into its message - is the else, reached when the username resolves to
                // nobody. Both are driven, or the message fix is covered by reading only.
                var orphanToken = "BL149-ORPHAN-" + Guid.NewGuid().ToString("N");
                var (orphanStatus, _) = await GateDrive.SendJsonAsync(
                    app, client, cookies: null, Host, actorUserId: null,
                    HttpMethod.Post, $"/api/User/reset?token={WebUtility.UrlEncode(orphanToken)}",
                    new User
                    {
                        Username = "recovery.nobody",
                        Password = "N3wPassw0rd!$2026",
                        DisplayName = "Nobody",
                        Email = "nobody@recovery.test",
                        SiteId = siteId,
                        TimeZoneId = "UTC",
                        CultureCode = "en-US",
                    },
                    _output, "reset (unknown user)");
                _output.WriteLine($"[reset unknown] {(int)orphanStatus}");

                var logs = new List<Log>();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(30))
                {
                    logs = await ReadLogsAsync(app, siteId);
                    if (logs.Any(row => (row.Message ?? "").Contains("recovery.reset", StringComparison.Ordinal)))
                    {
                        break;
                    }

                    await Task.Delay(250);
                }

                _output.WriteLine($"[logs] {logs.Count} row(s) after the failed reset");

                Assert.True(
                    logs.Any(row => (row.Message ?? "").Contains("recovery.reset", StringComparison.Ordinal)),
                    "the reset's own log entry never reached the table, so the token assertion below would be " +
                    "asserting nothing. Positive control failed.");

                var leaked = logs
                    .Where(row => Columns(row).Any(value =>
                        value?.Contains(token, StringComparison.Ordinal) == true ||
                        value?.Contains(orphanToken, StringComparison.Ordinal) == true))
                    .ToList();

                foreach (var row in leaked)
                {
                    _output.WriteLine($"[LEAK] {row.Level} {row.Function} | msg={row.Message} | url={row.Url} | props={row.Properties}");
                }

                Assert.True(leaked.Count == 0,
                    "A password-reset token reached the site Log table, where every administrator can read it. " +
                    "It must not appear in the message, the properties, or the URL the request arrived on.");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static string Trim(string value) =>
            string.IsNullOrEmpty(value) ? "(empty)" : (value.Length <= 300 ? value : value.Substring(0, 300) + " ...");

        /// <summary>Every string column of a log row — the token must be in none of them.</summary>
        private static IEnumerable<string?> Columns(Log row) =>
            new[] { row.Message, row.MessageTemplate, row.Properties, row.Url, row.Exception, row.Category, row.Feature, row.Function };

        /// <summary>
        /// Every log row for the site. The level and function filters must be NULL, not empty: the repository
        /// applies them with <c>if (level != null)</c>, so <c>""</c> asks for rows whose Level IS the empty
        /// string and matches nothing — which is how a "no token in the log" assertion can pass against a table
        /// it never read. The positive control in the test is what caught that.
        /// </summary>
        private static Task<List<Log>> ReadLogsAsync(Microsoft.AspNetCore.Builder.WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                (await sp.GetRequiredService<ILogRepository>().GetLogsAsync(siteId, null, null, 500)).ToList());
    }
}
