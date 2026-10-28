#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 increment 10 - the editor's trigger panel: a webhook's address and how a call is signed (never the secret), a
    /// schedule's next runs, and listening for one webhook call - which the receiver hands to the listeners of its site and name
    /// only once the call is authentic.
    /// </summary>
    public sealed class PipelineTriggerPanelTests
    {
        private sealed class At(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static Task<TriggerInfoView> Info(string type, string? value, PipelineTriggerOptions options, DateTimeOffset? now = null, string? zone = null,
            PipelineWebhookSecrets? secrets = null, int siteId = 1) =>
            new GetTriggerInfoQueryHandler(Options.Create(options), new At(now ?? new DateTimeOffset(2026, 10, 6, 2, 30, 0, TimeSpan.Zero)),
                    secrets ?? new PipelineWebhookSecrets(new Notifications.FakeSiteSecretStore()), new Stomme.Shared.SiteState { Alias = new Stomme.Models.Alias { TenantId = 1, SiteId = siteId } })
                .Handle(new GetTriggerInfoQuery(type, value, zone), CancellationToken.None);

        // D111a: a webhook's own secret - the receiver's choice (its own when it has one; none when that cannot be read, never the
        // installation's then; the installation's otherwise), per site and case-blind, and the panel saying which - never what.
        [Fact]
        public async Task A_webhook_with_its_own_secret_is_verified_under_it_alone_and_an_unreadable_one_refuses()
        {
            var store = new Notifications.FakeSiteSecretStore();
            var secrets = new PipelineWebhookSecrets(store);
            var webhooks = new WebhookTriggerOptions { Enabled = true, Secret = "installation" };
            var options = new PipelineTriggerOptions { Webhooks = webhooks };
            (string? Secret, string? Problem, bool Own) installation = ("installation", null, false);

            Assert.Equal(installation, await secrets.ForCallAsync(1, "orders", webhooks));
            var before = await Info("Stomme.Webhook", "orders", options, secrets: secrets);
            Assert.Equal((true, false, false), (before.Signed, before.OwnSecret, before.OwnSecretUnreadable));

            // Its own: 64 hex characters, for that site and name only - Orders and orders are one webhook.
            var own = await secrets.RenewAsync(1, "Orders");
            Assert.Matches("^[0-9a-f]{64}$", own);
            Assert.Equal((own, (string?)null, true), await secrets.ForCallAsync(1, "orders", webhooks));
            Assert.Equal(installation, await secrets.ForCallAsync(2, "orders", webhooks));
            Assert.Equal(installation, await secrets.ForCallAsync(1, "other", webhooks));
            var with = await Info("Stomme.Webhook", "ORDERS", options, secrets: secrets);
            Assert.Equal((true, true, false), (with.Signed, with.OwnSecret, with.OwnSecretUnreadable));
            Assert.DoesNotContain(own, JsonSerializer.Serialize(with));
            Assert.False((await Info("Stomme.Webhook", "orders", options, secrets: secrets, siteId: 2)).OwnSecret);

            // A new one replaces it.
            var renewed = await secrets.RenewAsync(1, "orders");
            Assert.NotEqual(own, renewed);
            Assert.Equal(renewed, (await secrets.ForCallAsync(1, "orders", webhooks)).Secret);

            // Unreadable (the key ring changed): no secret - not the installation's - and why; the panel says so.
            store.Unreadable.Add((1, PipelineWebhookSecrets.ReferenceKey("orders")));
            var unreadable = await secrets.ForCallAsync(1, "orders", webhooks);
            Assert.Equal(((string?)null, true), (unreadable.Secret, unreadable.Own));
            Assert.Contains("cannot be read", unreadable.Problem);
            var lost = await Info("Stomme.Webhook", "orders", options, secrets: secrets);
            Assert.Equal((false, false, true), (lost.Signed, lost.OwnSecret, lost.OwnSecretUnreadable));
            store.Unreadable.Clear();

            // Removed: the installation's again; a second removal says there was none.
            Assert.True(await secrets.RemoveAsync(1, "orders"));
            Assert.False(await secrets.RemoveAsync(1, "orders"));
            Assert.Equal(installation, await secrets.ForCallAsync(1, "orders", webhooks));

            // With no installation secret, a secret of its own alone makes a webhook receivable.
            var bare = new WebhookTriggerOptions { Enabled = true };
            Assert.Null((await secrets.ForCallAsync(1, "orders", bare)).Secret);
            var only = await secrets.RenewAsync(1, "orders");
            Assert.Equal(only, (await secrets.ForCallAsync(1, "orders", bare)).Secret);
            Assert.True((await Info("Stomme.Webhook", "orders", new PipelineTriggerOptions { Webhooks = bare }, secrets: secrets)).Signed);
        }

        // The store's key grammar refuses '_' and runs of '.' or '-', which a webhook name may carry: the key is the name hashed.
        [Fact]
        public void A_webhook_secret_key_fits_the_store_for_every_name_and_is_case_blind()
        {
            foreach (var name in new[] { "orders", "Orders", "a_b", "x..y", "-lead", "a.b-c_d", new string('z', 100) })
            {
                Assert.True(Stomme.Infrastructure.Security.SiteSecretStore.IsValidReferenceKey(PipelineWebhookSecrets.ReferenceKey(name)), name);
            }

            Assert.Equal(PipelineWebhookSecrets.ReferenceKey("orders"), PipelineWebhookSecrets.ReferenceKey(" Orders "));
            Assert.NotEqual(PipelineWebhookSecrets.ReferenceKey("orders"), PipelineWebhookSecrets.ReferenceKey("orders2"));
        }

        [Fact]
        public async Task A_webhook_says_its_address_its_signing_and_whether_the_site_receives_it_never_the_secret()
        {
            var options = new PipelineTriggerOptions
            {
                Webhooks = { Enabled = true, Secret = "the-secret-itself", Endpoints = { ["orders"] = new WebhookEndpointOptions { AllowedCallers = new List<string> { "10.0.0.0/8" } } } },
            };

            var orders = await Info("Stomme.Webhook", "Orders", options);
            Assert.Equal(("Webhook", (string?)null, true, true, true), (orders.Kind, orders.Error, orders.Enabled, orders.Signed, orders.Allowlisted));
            Assert.Equal("api/Pipelines/webhooks/orders", orders.Path);
            Assert.Equal("X-Signature-256", orders.SignatureHeader);
            Assert.DoesNotContain("the-secret-itself", JsonSerializer.Serialize(orders));

            var other = await Info("Stomme.Webhook", "other", options);
            Assert.False(other.Allowlisted);

            var bad = await Info("Stomme.Webhook", "no spaces!", options);
            Assert.Contains("is not a webhook name", bad.Error);
            Assert.Null(bad.Path);

            var off = await Info("Stomme.Webhook", "orders", new PipelineTriggerOptions());
            Assert.Equal((false, false), (off.Enabled, off.Signed));
        }

        // A mounted secret (the deployment's form, as Backup:Schedule:PassphraseFile): the file wins over Secret, read at each
        // call, its trailing line breaks trimmed and nothing else; a file named but missing or empty refuses every call - never
        // unsigned - and says why; and the panel's Signed follows it.
        [Fact]
        public async Task A_secret_file_wins_and_a_missing_or_empty_one_refuses_every_call()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-webhook-secret-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                var file = System.IO.Path.Combine(folder, "webhook-secret");
                System.IO.File.WriteAllText(file, " from-the-file \r\n");
                var webhooks = new WebhookTriggerOptions { Enabled = true, Secret = "from-the-setting", SecretFile = file };
                Assert.Equal(" from-the-file ", webhooks.EffectiveSecret(out var problem)); // spaces are the secret's own
                Assert.Null(problem);
                var body = System.Text.Encoding.UTF8.GetBytes("{}");
                Assert.True(PipelineWebhooks.VerifySignature(webhooks.EffectiveSecret(out _), body, PipelineWebhooks.ComputeSignature(" from-the-file ", body)));
                Assert.False(PipelineWebhooks.VerifySignature(webhooks.EffectiveSecret(out _), body, PipelineWebhooks.ComputeSignature("from-the-setting", body)));
                var withFile = await Info("Stomme.Webhook", "orders", new PipelineTriggerOptions { Webhooks = webhooks });
                Assert.True(withFile.Signed);
                Assert.True(withFile.SecretFromFile);

                System.IO.File.WriteAllText(file, "\n");
                Assert.Null(webhooks.EffectiveSecret(out problem));
                Assert.Contains("which is empty", problem);

                webhooks.SecretFile = System.IO.Path.Combine(folder, "not-there");
                Assert.Null(webhooks.EffectiveSecret(out problem)); // never the Secret beside it
                Assert.Contains("cannot be read", problem);
                Assert.False(PipelineWebhooks.VerifySignature(webhooks.EffectiveSecret(out _), body, PipelineWebhooks.ComputeSignature("from-the-setting", body)));
                // Not signed, and said to be the file's fault - not a secret never configured.
                var missingFile = await Info("Stomme.Webhook", "orders", new PipelineTriggerOptions { Webhooks = webhooks });
                Assert.Equal((false, true), (missingFile.Signed, missingFile.SecretFromFile));
                var none = await Info("Stomme.Webhook", "orders", new PipelineTriggerOptions { Webhooks = new() { Enabled = true } });
                Assert.Equal((false, false), (none.Signed, none.SecretFromFile));

                Assert.Equal("from-the-setting", new WebhookTriggerOptions { Secret = "from-the-setting" }.EffectiveSecret(out _));
                Assert.Null(new WebhookTriggerOptions().EffectiveSecret(out problem));
                Assert.Contains("no Secret or SecretFile", problem);
            }
            finally
            {
                System.IO.Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public async Task A_schedule_says_its_next_runs_or_why_its_cron_is_not_one()
        {
            var options = new PipelineTriggerOptions { Schedules = { IntervalSeconds = 30 } };

            var every6 = await Info("Stomme.Schedule", " 0  */6 * * * ", options);
            Assert.Null(every6.Error);
            Assert.Equal(new[] { "2026-10-06 06:00", "2026-10-06 12:00", "2026-10-06 18:00", "2026-10-07 00:00", "2026-10-07 06:00" },
                every6.NextRunsUtc.Select(run => run.ToString("yyyy-MM-dd HH:mm")));
            Assert.All(every6.NextRunsUtc, run => Assert.Equal(DateTimeKind.Utc, run.Kind));
            Assert.Equal((true, 30), (every6.Enabled, every6.CheckedEverySeconds));

            var bad = await Info("Stomme.Schedule", "61 * * * *", options);
            Assert.Contains("is not a five-field cron expression", bad.Error);
            Assert.Empty(bad.NextRunsUtc);
            Assert.Contains("needs a cron expression", (await Info("Stomme.Schedule", "", options)).Error);

            Assert.Equal("", (await Info("Stomme.WriteLine", "x", options)).Kind);

            // BL-229, n8n's trigger rules: several lines, their next runs merged in order; a bad second line named.
            var ruled = await Info("Stomme.Schedule", "0 6 * * *\n30 7 * * *", options);
            Assert.Equal(new[] { "2026-10-06 06:00", "2026-10-06 07:30", "2026-10-07 06:00", "2026-10-07 07:30", "2026-10-08 06:00" },
                ruled.NextRunsUtc.Select(run => run.ToString("yyyy-MM-dd HH:mm")));
            Assert.StartsWith("'61 * * * *' is not a five-field cron expression", (await Info("Stomme.Schedule", "0 6 * * *\n61 * * * *", options)).Error);

            // BL-229: in a time zone, daylight saving followed - six in the morning in Stockholm is 04:00 UTC until the clocks go
            // back on 2026-10-25, 05:00 UTC after; each run also as the zone's clock reads it. A zone it does not know is said.
            var stockholm = await Info("Stomme.Schedule", "0 6 * * *", options, new DateTimeOffset(2026, 10, 24, 2, 30, 0, TimeSpan.Zero), "Europe/Stockholm");
            Assert.Null(stockholm.Error);
            Assert.Equal("Europe/Stockholm", stockholm.TimeZone);
            Assert.Equal(new[] { "2026-10-24 04:00", "2026-10-25 05:00", "2026-10-26 05:00" }, stockholm.NextRunsUtc.Take(3).Select(run => run.ToString("yyyy-MM-dd HH:mm")));
            Assert.All(stockholm.NextRunsInZone, run => Assert.Equal("06:00", run.ToString("HH:mm")));
            Assert.Null((await Info("Stomme.Schedule", "0 6 * * *", options, zone: "UTC")).TimeZone);
            Assert.Contains("is not a time zone this server knows", (await Info("Stomme.Schedule", "0 6 * * *", options, zone: "Mars/Olympus_Mons")).Error);
        }

        [Fact]
        public async Task A_listener_hears_the_next_call_to_its_key_only_and_hears_nothing_when_none_comes()
        {
            var listeners = new PipelineWebhookListeners();
            var key = PipelineWebhookListeners.Key(1, 1, "orders");

            var first = listeners.ListenAsync(key, TimeSpan.FromSeconds(10), CancellationToken.None);
            var second = listeners.ListenAsync(key, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(0, listeners.Offer(PipelineWebhookListeners.Key(1, 2, "orders"), DateTime.UtcNow, "application/json", "another site's"));
            Assert.False(first.IsCompleted);
            Assert.Equal(2, listeners.Offer(key, new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc), "application/json", "{\"order\":1}"));

            foreach (var heard in new[] { await first, await second })
            {
                Assert.Equal((true, "application/json", "{\"order\":1}", false), (heard.Received, heard.ContentType, heard.Body, heard.Truncated));
            }

            // Heard once: the next call reaches no one, and a listener with no call hears that none came.
            Assert.Equal(0, listeners.Offer(key, DateTime.UtcNow, null, "late"));
            Assert.False((await listeners.ListenAsync(key, TimeSpan.FromMilliseconds(50), CancellationToken.None)).Received);

            // A long body is cut to what the editor shows, and says so.
            var long_ = listeners.ListenAsync(key, TimeSpan.FromSeconds(10), CancellationToken.None);
            listeners.Offer(key, DateTime.UtcNow, "text/plain", new string('x', WebhookCallView.MaxBodyChars + 10));
            var cut = await long_;
            Assert.Equal((WebhookCallView.MaxBodyChars, true), (cut.Body.Length, cut.Truncated));
        }

        [Fact]
        public async Task A_name_admits_a_few_listeners_at_once()
        {
            var listeners = new PipelineWebhookListeners();
            var key = PipelineWebhookListeners.Key(1, 1, "busy");
            using var stop = new CancellationTokenSource();
            var waiting = Enumerable.Range(0, PipelineWebhookListeners.MaxPerName).Select(_ => listeners.ListenAsync(key, TimeSpan.FromSeconds(30), stop.Token)).ToList();

            await Assert.ThrowsAsync<InvalidOperationException>(() => listeners.ListenAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None));

            stop.Cancel();
            Assert.All(await Task.WhenAll(waiting), heard => Assert.False(heard.Received));
            // Gone, they make room again.
            Assert.False((await listeners.ListenAsync(key, TimeSpan.FromMilliseconds(20), CancellationToken.None)).Received);
        }
    }

    /// <summary>
    /// The trigger panel DRIVEN over the real host on an installed SQLite site: the info endpoint, and an author listening while
    /// a caller posts - an unsigned call is refused and never heard, a signed one is heard with what it sent, no pipeline needed.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineTriggerPanelDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Secret = "panel-webhook-secret";

        [Fact]
        public async Task An_author_listening_hears_a_signed_call_and_never_an_unsigned_one()
        {
            var pipelines = new JsonObject { ["Enabled"] = true, ["Webhooks"] = new JsonObject { ["Enabled"] = true, ["Secret"] = Secret } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: new JsonObject { ["Pipelines"] = pipelines });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var info = await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/triggers/info?type=Stomme.Webhook&value=Incoming", output, "info");
                Assert.Equal(HttpStatusCode.OK, info.Status);
                var view = JsonSerializer.Deserialize<TriggerInfoView>(info.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal(("api/Pipelines/webhooks/incoming", true, true), (view.Path, view.Enabled, view.Signed));
                Assert.DoesNotContain(Secret, info.Body);

                // Nobody signed in: no listening.
                using var anonymous = GateDrive.NewClient(app, Host);
                var refused = await GateDrive.SendJsonAsync(app, anonymous, null, Host, null, HttpMethod.Post, "/api/Pipelines/triggers/webhooks/incoming/listen?seconds=5", new { }, output, "anonymous listen");
                Assert.True(refused.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, refused.Status.ToString());

                var listening = GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines/triggers/webhooks/incoming/listen?seconds=20", new { }, output, "listen");
                await Task.Delay(1000); // the listener registers as the request arrives

                using var caller = GateDrive.NewClient(app, Host);
                var unsigned = Encoding.UTF8.GetBytes("{\"order\":\"forged\"}");
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(caller, "incoming", unsigned, null)).StatusCode);
                var signed = Encoding.UTF8.GetBytes("{\"order\":42}");
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, "incoming", signed, PipelineWebhooks.ComputeSignature(Secret, signed))).StatusCode);

                var heard = await listening;
                Assert.Equal(HttpStatusCode.OK, heard.Status);
                var call = JsonSerializer.Deserialize<WebhookCallView>(heard.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal((true, "{\"order\":42}"), (call.Received, call.Body));
                Assert.StartsWith("application/json", call.ContentType);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string name, byte[] body, string? signature)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + Host), "/api/Pipelines/webhooks/" + name));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (signature is not null)
            {
                request.Headers.TryAddWithoutValidation(PipelineWebhooks.SignatureHeaderName, signature);
            }

            return await client.SendAsync(request);
        }
    }
}
