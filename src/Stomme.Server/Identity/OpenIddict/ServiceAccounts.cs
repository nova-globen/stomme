using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// BL-224 (D61's correction and its amendment): a client-credentials application's SERVICE ACCOUNT - the Stomme user a
    /// token with no person behind it signs in on <c>api/*</c>, judged by its own roles and permission rows. The binding is
    /// the account's NAME, derived from the client id here, and its LOCKOUT, which only <see cref="LockOutForGoodAsync"/> sets
    /// and no admin page can: an account of that name that is not locked out for good (a person who registered the name) is
    /// never used. Nothing is stored on the application, whose descriptor an edit or the config seeder rebuilds.
    /// </summary>
    public static class ServiceAccounts
    {
        public const string SwitchKey = "OpenIddict:ServiceAccounts:Enabled";

        /// <summary>The address domain of every service account: <c>.invalid</c> is reserved and receives no mail (RFC 2606).</summary>
        public const string EmailDomain = "service.invalid";

        /// <summary>On only when the authority is on AND the switch is (default off).</summary>
        public static bool IsEnabled(IConfiguration configuration) =>
            configuration.IsOpenIddictEnabled() && configuration.GetValue(SwitchKey, false);

        /// <summary>
        /// <c>svc-</c> + the client id normalised to letters, digits, <c>-</c>, <c>_</c> and <c>.</c> (at most 40 of them) +
        /// <c>-</c> + six hex characters of a SHA-256 of the EXACT id, so two ids that normalise alike never share an account.
        /// </summary>
        public static string UsernameFor(string clientId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
            var normalised = new StringBuilder();
            foreach (var character in clientId.Trim().ToLowerInvariant())
            {
                normalised.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-');
                if (normalised.Length == 40)
                {
                    break;
                }
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId))).ToLowerInvariant()[..6];
            return $"svc-{normalised}-{hash}";
        }

        public static string EmailFor(string username) => $"{username}@{EmailDomain}";

        /// <summary>The marker only the server sets: lockout enabled and ending never.</summary>
        public static async Task<bool> IsLockedOutForGoodAsync(UserManager<IdentityUser> users, IdentityUser user) =>
            await users.GetLockoutEnabledAsync(user) && await users.GetLockoutEndDateAsync(user) == DateTimeOffset.MaxValue;

        /// <summary>Locks the account out of every sign-in, for good.</summary>
        public static async Task<bool> LockOutForGoodAsync(UserManager<IdentityUser> users, IdentityUser user) =>
            (await users.SetLockoutEnabledAsync(user, true)).Succeeded
            && (await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue)).Succeeded;

        /// <summary>A password nobody is told: 32 random bytes, with a character of every class a site's policy may ask for.</summary>
        public static string NewUndisclosedPassword() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "aZ9!";
    }
}
