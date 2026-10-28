using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-90: <c>LoginOptions:AllowPasswordRecovery</c> turns self-service recovery off AT THE SERVER.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hiding the links was never enough: <c>api/User/forgotpassword/{username}</c> and
    /// <c>forgotusername/{email}</c> are anonymous by necessity, and they minted a password-reset token and
    /// queued mail for anyone who guessed a username, with no setting able to stop them.
    /// </para>
    /// <para>
    /// Driven over real HTTP as an anonymous caller against an installed site, and asserted at the effect that
    /// matters - the Notification row that carries the reset link. Both directions: with the setting absent
    /// (the stock install) recovery still works, so the switch cannot be mistaken for "recovery is broken".
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class PasswordRecoverySettingTests
    {
        private const string Host = EntityPermissionWriteValidationTests.Host;
        private const string Password = "R3cover!Me$2026";

        private readonly ITestOutputHelper _output;

        public PasswordRecoverySettingTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Recovery_is_refused_at_the_server_when_the_site_turns_it_off()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var user = await GateDrive.AddUserAsync(app, Host, "bl90user", Password, "bl90user@gate.test");
                using var anonymous = GateDrive.NewClient(app, Host);

                // ---- (a) the stock install: recovery still works ------------------------------------------
                var before = await NotificationCountAsync(app, user.UserId);
                var allowed = await GateDrive.GetAsync(anonymous, null, Host, $"/api/User/forgotpassword/{user.Username}", _output, "forgot password (setting absent)");
                var afterAllowed = await NotificationCountAsync(app, user.UserId);
                _output.WriteLine($"[absent] {(int)allowed.Status} body='{allowed.Body}' notifications {before} -> {afterAllowed}");
                Assert.True(afterAllowed > before, "with no setting, recovery must still queue its mail");

                // ---- (b) turned off: the endpoint refuses, and nothing is queued ---------------------------
                await SetRecoveryAsync(app, "false");
                var refusedCount = await NotificationCountAsync(app, user.UserId);
                var refused = await GateDrive.GetAsync(anonymous, null, Host, $"/api/User/forgotpassword/{user.Username}", _output, "forgot password (off)");
                var refusedUsername = await GateDrive.GetAsync(anonymous, null, Host, $"/api/User/forgotusername/{user.Email}", _output, "forgot username (off)");
                var afterRefused = await NotificationCountAsync(app, user.UserId);
                _output.WriteLine($"[off] password='{refused.Body}' username='{refusedUsername.Body}' notifications {refusedCount} -> {afterRefused}");

                Assert.Equal("false", refused.Body.Trim().ToLowerInvariant());
                Assert.Equal("false", refusedUsername.Body.Trim().ToLowerInvariant());
                Assert.Equal(refusedCount, afterRefused);

                // ---- (c) turned back on: it works again ----------------------------------------------------
                await SetRecoveryAsync(app, "true");
                var onCount = await NotificationCountAsync(app, user.UserId);
                await GateDrive.GetAsync(anonymous, null, Host, $"/api/User/forgotpassword/{user.Username}", _output, "forgot password (on)");
                var afterOn = await NotificationCountAsync(app, user.UserId);
                _output.WriteLine($"[on] notifications {onCount} -> {afterOn}");
                Assert.True(afterOn > onCount, "turning the setting back on must restore recovery");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task SetRecoveryAsync(Microsoft.AspNetCore.Builder.WebApplication app, string value) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var settings = sp.GetRequiredService<ISettingRepository>();
                var existing = (await settings.GetSettingsAsync(EntityNames.Site, alias.SiteId))
                    .FirstOrDefault(item => item.SettingName == "LoginOptions:AllowPasswordRecovery");
                if (existing == null)
                {
                    await settings.AddSettingAsync(new Setting
                    {
                        EntityName = EntityNames.Site,
                        EntityId = alias.SiteId,
                        SettingName = "LoginOptions:AllowPasswordRecovery",
                        SettingValue = value,
                        IsPrivate = false
                    });
                }
                else
                {
                    existing.SettingValue = value;
                    await settings.UpdateSettingAsync(existing);
                }

                // The site (and its settings dictionary) is cached per alias, so the change is published the way
                // the product does it - through the sync manager - rather than by reaching into the cache.
                await sp.GetRequiredService<ISyncManager>().AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                return true;
            });

        // Counted through the user's MAILBOX, never the delivery queue (BL-158). The queue read filters on
        // `SendOn < UtcNow` - strictly - while a new notification is stamped `SendOn = CreatedOn = UtcNow`, so a row is
        // invisible to the queue for the instant it is created. MEASURED: 58 of 300 add-then-count pairs missed the row,
        // and all 58 saw it 20 ms later. That failed this test about three full runs in seven, always on the last, warm,
        // fastest leg. It also made the "nothing was queued" assertion below WEAK: a mail queued against the rule could
        // have been invisible to the very read that was supposed to catch it. The mailbox read has no such filter.
        private static Task<int> NotificationCountAsync(Microsoft.AspNetCore.Builder.WebApplication app, int toUserId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                (await sp.GetRequiredService<INotificationRepository>().GetNotificationsAsync(alias.SiteId, -1, toUserId)).Count());
    }
}
