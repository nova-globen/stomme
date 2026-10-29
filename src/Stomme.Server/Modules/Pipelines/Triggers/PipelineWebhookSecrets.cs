#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Pipelines.Steps;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// A webhook's OWN secret (owner 2026-10-10): a secret for one webhook name of one site, kept in the site's secret
    /// store and made by the editor - so a pipeline's author can hand one caller a secret that opens one webhook, and
    /// renew it without a host's configuration change. Signing is HMAC as before. A name with no secret of its own takes the
    /// installation's (<c>Pipelines:Webhooks:Secret</c> or <c>SecretFile</c>), so the callers set up before this keep working;
    /// a name WITH one is verified under it alone, and when it cannot be read (the host's key ring changed) every call is
    /// refused rather than falling back to the installation's, which would widen what opens it.
    /// </summary>
    public sealed class PipelineWebhookSecrets(ISiteSecretStore secrets)
    {
        /// <summary>The bytes of randomness in a secret made here; it is given as hex, 64 characters.</summary>
        public const int SecretBytes = 32;

        /// <summary>
        /// The store's key for a webhook name: its normalised name hashed, since a name may carry '_' and runs of '.' or '-'
        /// that the store's key grammar refuses. Case-blind as the receiver is: <c>Orders</c> and <c>orders</c> are one webhook.
        /// </summary>
        public static string ReferenceKey(string name) =>
            "pipelines.webhook." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Webhook.Normalize(name))))[..32] + ".secret";

        public Task<SiteSecretResult> GetAsync(int siteId, string name, CancellationToken ct = default) =>
            secrets.GetAsync(siteId, ReferenceKey(name), ct);

        /// <summary>Makes a new secret for the name, replacing any it had, and returns it - the only time it is ever given out.</summary>
        public async Task<string> RenewAsync(int siteId, string name, CancellationToken ct = default)
        {
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SecretBytes));
            await secrets.SetAsync(siteId, ReferenceKey(name), secret, ct);
            return secret;
        }

        /// <summary>Removes the name's own secret, so it takes the installation's again; false when it had none.</summary>
        public async Task<bool> RemoveAsync(int siteId, string name, CancellationToken ct = default)
        {
            var key = ReferenceKey(name);
            if ((await secrets.GetAsync(siteId, key, ct)).Status == SiteSecretStatus.NotFound)
            {
                return false;
            }

            await secrets.DeleteAsync(siteId, key, ct);
            return true;
        }

        /// <summary>
        /// The secret a call to <paramref name="name"/> on the site is verified under, and - when there is none - why: the name's
        /// own when it has one, none when that cannot be read, the installation's otherwise. Never the secret in the reason.
        /// </summary>
        public async Task<(string? Secret, string? Problem, bool Own)> ForCallAsync(int? siteId, string name, WebhookTriggerOptions settings, CancellationToken ct = default)
        {
            if (siteId is { } site)
            {
                var own = await GetAsync(site, name, ct);
                if (own.Status == SiteSecretStatus.Found)
                {
                    return (own.Secret, null, true);
                }

                if (own.Status == SiteSecretStatus.Unreadable)
                {
                    return (null, "its own secret cannot be read (the host's key ring changed) - make a new one in the editor.", true);
                }
            }

            var installation = settings.EffectiveSecret(out var problem); // the mounted file's, or the setting's; none refuses every call
            return (installation, problem, false);
        }
    }
}
