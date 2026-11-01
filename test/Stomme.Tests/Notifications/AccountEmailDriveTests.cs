#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Managers;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The account e-mails through real SMTP (BL-279): the site sends through MailKit to a loopback SMTP sink with a login
    /// of its own, and the test follows the link each message carries. Confirm the address (a user added unconfirmed), reset
    /// the password (and sign in with the new one), and the login link (which signs the person in). Before, tests faked mail
    /// at the notification-channel level and none of this was driven end to end.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AccountEmailDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Username = "mailuser";
        private const string Email = "mailuser@gate.test";
        private const string Password = "M4il!User$2026";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task Confirm_reset_and_login_link_mails_arrive_through_smtp_and_their_links_work()
        {
            using var smtp = new ScriptedSmtpServer();
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    foreach (var (name, value, isPrivate) in new[]
                    {
                        ("SMTPHost", "127.0.0.1", false), ("SMTPPort", smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
                        ("SMTPSSL", "None", false), ("SMTPAuthentication", "Basic", false), ("SMTPSender", "noreply@gate.test", false),
                        ("SMTPUsername", smtp.Username, true), ("SMTPPassword", smtp.Password, true), ("LoginOptions:LoginLink", "true", false),
                    })
                    {
                        await settings.AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = alias.SiteId, SettingName = name, SettingValue = value, IsPrivate = isPrivate });
                    }

                    await sp.GetRequiredService<ISyncManager>().AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                    return 0;
                });

                // The verify and reset bodies below carry no SiteId, as a hand-made API call may: that read must not poison the
                // user's cached roles for their own site (BL-279 found it did, and the person could then not sign in).
                // 1. A user added unconfirmed is sent a link to confirm the address; the link's token confirms it.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    await sp.GetRequiredService<IUserManager>().AddUser(new User { Username = Username, Password = Password, Email = Email, DisplayName = Username, SiteId = alias.SiteId, EmailConfirmed = false }));
                var confirm = await DeliverAsync(app, smtp, 1);
                var (confirmName, confirmToken) = Link(confirm, @"/login\?name=([^&\s""<]+)&token=([^\s""<&]+)");
                Assert.Equal(Username, confirmName);
                using var anonymous = GateDrive.NewClient(app, Host);
                var verified = await GateDrive.SendJsonAsync(app, anonymous, null, Host, null, HttpMethod.Post, "/api/User/verify?token=" + WebUtility.UrlEncode(confirmToken),
                    new User { Username = Username, Password = "", Email = Email, DisplayName = Username }, output, "verify the address");
                Assert.Equal(HttpStatusCode.OK, verified.Status);
                Assert.NotEqual("null", verified.Body.Trim()); // a refused token answers null
                Assert.True(await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>().FindByNameAsync(Username))!.EmailConfirmed), "the address is not confirmed");

                // 2. Forgot password: a reset link; the token sets a new password, and the new one signs in.
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(anonymous, null, Host, "/api/User/forgotpassword/" + Username, output, "forgot password")).Status);
                var reset = await DeliverAsync(app, smtp, 2);
                var (resetName, resetToken) = Link(reset, @"/reset\?name=([^&\s""<]+)&token=([^\s""<&]+)");
                Assert.Equal(Username, resetName);
                const string newPassword = "N3w!Mail$2026";
                var changed = await GateDrive.SendJsonAsync(app, anonymous, null, Host, null, HttpMethod.Post, "/api/User/reset?token=" + WebUtility.UrlEncode(resetToken),
                    new User { Username = Username, Password = newPassword, Email = Email, DisplayName = Username }, output, "reset the password");
                Assert.Equal(HttpStatusCode.OK, changed.Status);
                output.WriteLine("[reset answer] " + changed.Body);
                Assert.NotEqual("null", changed.Body.Trim()); // a refused token or password answers null
                Assert.True(await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var identity = sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
                    return await identity.CheckPasswordAsync((await identity.FindByNameAsync(Username))!, newPassword);
                }), "the new password was not set");
                using var signIn = GateDrive.NewClient(app, Host);
                await GateDrive.LoginAsync(app, signIn, Host, Username, newPassword, output);

                // 3. The login link signs the person in, with no password.
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(anonymous, null, Host, "/api/User/loginlink/" + WebUtility.UrlEncode(Email) + "?returnurl=%2F", output, "ask for a login link")).Status);
                var linkMail = await DeliverAsync(app, smtp, 3);
                var link = Regex.Match(linkMail.TextBody ?? linkMail.HtmlBody ?? "", @"/pages/loginlink\?[^\s""<]+");
                Assert.True(link.Success, "no login link in: " + (linkMail.TextBody ?? linkMail.HtmlBody));
                using var linkClient = GateDrive.NewClient(app, Host);
                var followed = await linkClient.GetAsync(WebUtility.HtmlDecode(link.Value));
                var setCookies = followed.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : new();
                output.WriteLine($"login link -> {(int)followed.StatusCode} {followed.Headers.Location}; cookies {string.Join(", ", setCookies.Select(c => c.Split('=')[0]))}");
                Assert.Contains(setCookies, cookie => cookie.Contains("Identity.Application", StringComparison.Ordinal));

                // An address with no account is answered the same, and nothing is sent (no account-existence oracle).
                var unknown = await GateDrive.GetAsync(anonymous, null, Host, "/api/User/loginlink/" + WebUtility.UrlEncode("nobody@gate.test") + "?returnurl=%2F", output, "a login link for no account");
                Assert.Equal("true", unknown.Body.Trim());
                await GateDrive.InTenantAsync(app, Host, (sp, _) => new NotificationJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                Assert.False(await smtp.WaitForAsync(4, TimeSpan.FromSeconds(2)), "a message was sent for an address with no account: " + string.Join(" | ", smtp.Messages.Select(m => m.Subject + " -> " + m.To + ": " + (m.TextBody ?? m.HtmlBody))));

                Assert.All(smtp.Messages, message => Assert.Equal(Email, message.To.Mailboxes.Single().Address));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>Runs the Notification Job once and waits for the <paramref name="count"/>th message; that message.</summary>
        private async Task<MimeMessage> DeliverAsync(Microsoft.AspNetCore.Builder.WebApplication app, ScriptedSmtpServer smtp, int count)
        {
            var log = await GateDrive.InTenantAsync(app, Host, (sp, _) => new NotificationJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
            output.WriteLine("[notification job] " + log);
            Assert.True(await smtp.WaitForAsync(count, TimeSpan.FromSeconds(10)), $"message {count} did not arrive; job log: {log}");
            var message = smtp.Messages.ElementAt(count - 1);
            output.WriteLine($"[mail {count}] {message.Subject}");
            return message;
        }

        private static (string Name, string Token) Link(MimeMessage message, string pattern)
        {
            var body = WebUtility.HtmlDecode(message.TextBody ?? message.HtmlBody ?? "");
            var match = Regex.Match(body, pattern);
            Assert.True(match.Success, $"no link matching {pattern} in: {body}");
            return (match.Groups[1].Value, WebUtility.UrlDecode(match.Groups[2].Value));
        }
    }
}
