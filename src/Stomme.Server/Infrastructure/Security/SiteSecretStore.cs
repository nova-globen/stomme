#nullable enable
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// THE shared per-site secret store (BL-78 CORRECTION / D101 - one seam, not four): a credential a site-scoped
    /// feature needs at runtime - a notification channel's webhook token, an MCP server's API key, BL-81's
    /// and BL-96's credentials - is written here once, by REFERENCE KEY, and the feature's own aggregate stores only
    /// that key, never the secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Storage.</b> A PRIVATE Site setting row (<c>EntityName = Site</c>, <c>EntityId = siteId</c>,
    /// <c>SettingName = referenceKey</c>, <c>IsPrivate = true</c>) whose value is the secret PROTECTED through
    /// <see cref="ISecretProtector"/> (ASP.NET Data Protection). It therefore rides the Setting seam on every
    /// provider (EF and RavenDB) with no store of its own - which also means NO reflective parity gate discovers it
    /// (RavenFrameworkStoreRegistrationTests scans Stomme.Repository.Stores only); <c>SiteSecretStoreTests</c>
    /// drives SQLite, PostgreSQL and RavenDB instead.
    /// </para>
    /// <para>
    /// <b>The settings API cannot reach these rows.</b> A private Site row in this store's reference-key
    /// namespace is refused by every door of <c>SettingController</c> - list, get, post, put (by id, by name, in bulk),
    /// delete and import - for EVERY caller, the Host included, matching the name case-insensitively. Before that, the
    /// API's Administrator gate let a site Administrator without the module's own key read the ciphertext, overwrite the
    /// secret or delete it (measured, <c>SiteSecretSettingsApiTests</c>). The SynchronizationJob still replicates the row
    /// like any other setting.
    /// </para>
    /// <para>
    /// <b>Owner-visible residue (F8, not fixed here).</b> Nothing in the repository persists the Data Protection
    /// key ring (no <c>PersistKeysTo*</c>), and the protector purpose is the pre-existing
    /// <c>"Stomme.Identity.Secrets.v1"</c>. Where the key ring is ephemeral - a container redeployed without a
    /// persisted key directory - every stored secret becomes unreadable: durable data loss. A read of such a value
    /// returns <see cref="SiteSecretStatus.Unreadable"/> (it never throws, never leaks the ciphertext or the
    /// cryptographic exception message), so a caller records a clean failure and an operator re-enters the secret.
    /// </para>
    /// </remarks>
    public interface ISiteSecretStore
    {
        /// <summary>Protects and stores <paramref name="secret"/> for the site under <paramref name="referenceKey"/>, replacing any previous value.</summary>
        /// <exception cref="ArgumentException">The reference key is malformed, or the secret is null or empty.</exception>
        Task SetAsync(int siteId, string referenceKey, string secret, CancellationToken ct = default);

        /// <summary>Reads the secret for the site. Never throws for a missing or unreadable value - see <see cref="SiteSecretResult"/>.</summary>
        /// <exception cref="ArgumentException">The reference key is malformed.</exception>
        Task<SiteSecretResult> GetAsync(int siteId, string referenceKey, CancellationToken ct = default);

        /// <summary>Removes the secret for the site. Idempotent.</summary>
        /// <exception cref="ArgumentException">The reference key is malformed.</exception>
        Task DeleteAsync(int siteId, string referenceKey, CancellationToken ct = default);
    }

    /// <summary>How a <see cref="ISiteSecretStore.GetAsync"/> read ended.</summary>
    public enum SiteSecretStatus
    {
        /// <summary>The secret was found and unprotected; <see cref="SiteSecretResult.Secret"/> carries it.</summary>
        Found = 0,

        /// <summary>No secret is stored for this site under the key.</summary>
        NotFound = 1,

        /// <summary>
        /// A value is stored but cannot be unprotected - tampered, not a protected payload, or protected under a key
        /// ring this host no longer has (F8). The caller should fail the operation cleanly and ask for the secret again.
        /// </summary>
        Unreadable = 2,
    }

    /// <summary>The typed outcome of a secret read. <see cref="Secret"/> is non-null only when <see cref="Status"/> is <see cref="SiteSecretStatus.Found"/>.</summary>
    public readonly record struct SiteSecretResult(SiteSecretStatus Status, string? Secret)
    {
        public static SiteSecretResult NotFound => new(SiteSecretStatus.NotFound, null);

        public static SiteSecretResult Unreadable => new(SiteSecretStatus.Unreadable, null);

        public static SiteSecretResult Found(string secret) => new(SiteSecretStatus.Found, secret);

        public bool IsFound => Status == SiteSecretStatus.Found;

        // A result must never print the secret (a logged result would leak it).
        public override string ToString() => $"SiteSecretResult {{ Status = {Status} }}";
    }

    /// <summary>The Setting-backed <see cref="ISiteSecretStore"/>.</summary>
    public sealed class SiteSecretStore : ISiteSecretStore
    {
        /// <summary>The <c>setting_name</c> column bound (200, verified on a live migrated PostgreSQL 2026-09-14).</summary>
        public const int MaxReferenceKeyLength = 200;

        // Lower-case, dotted, at least two segments; a segment is [a-z0-9] runs joined by single hyphens:
        // notifications.channel.{id:N}.credential, mcp.server.{id:N}.api-key. Disjoint from the framework's own
        // PascalCase site setting names, so a secret cannot shadow (or be shadowed by) an ordinary setting.
        private static readonly Regex ReferenceKeyPattern = new(
            "^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)+$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly ISettingRepository _settings;
        private readonly ISecretProtector _protector;
        private readonly ILogger<SiteSecretStore> _logger;

        public SiteSecretStore(ISettingRepository settings, ISecretProtector protector, ILogger<SiteSecretStore> logger)
        {
            _settings = settings;
            _protector = protector;
            _logger = logger;
        }

        /// <summary>True when <paramref name="referenceKey"/> is a well-formed reference key.</summary>
        public static bool IsValidReferenceKey(string? referenceKey) =>
            !string.IsNullOrEmpty(referenceKey)
            && referenceKey.Length <= MaxReferenceKeyLength
            && ReferenceKeyPattern.IsMatch(referenceKey);

        public async Task SetAsync(int siteId, string referenceKey, string secret, CancellationToken ct = default)
        {
            EnsureValid(referenceKey);
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("A secret cannot be null or empty; delete the reference instead.", nameof(secret));
            }

            ct.ThrowIfCancellationRequested();
            var protectedValue = _protector.Protect(secret);

            var existing = await _settings.GetSettingAsync(EntityNames.Site, siteId, referenceKey);
            if (existing is null)
            {
                await _settings.AddSettingAsync(new Setting
                {
                    EntityName = EntityNames.Site,
                    EntityId = siteId,
                    SettingName = referenceKey,
                    SettingValue = protectedValue,
                    IsPrivate = true,
                });
                return;
            }

            existing.SettingValue = protectedValue;
            existing.IsPrivate = true;
            await _settings.UpdateSettingAsync(existing);
        }

        public async Task<SiteSecretResult> GetAsync(int siteId, string referenceKey, CancellationToken ct = default)
        {
            EnsureValid(referenceKey);
            ct.ThrowIfCancellationRequested();

            var setting = await _settings.GetSettingAsync(EntityNames.Site, siteId, referenceKey);
            if (setting is null)
            {
                return SiteSecretResult.NotFound;
            }

            if (string.IsNullOrEmpty(setting.SettingValue))
            {
                _logger.LogWarning("Site {SiteId} secret '{ReferenceKey}' is stored empty; it reads as unreadable.", siteId, referenceKey);
                return SiteSecretResult.Unreadable;
            }

            try
            {
                return SiteSecretResult.Found(_protector.Unprotect(setting.SettingValue));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The exception TYPE only: a cryptographic message can echo payload details, and the ciphertext
                // itself is never logged or returned.
                _logger.LogWarning(
                    "Site {SiteId} secret '{ReferenceKey}' cannot be unprotected ({ExceptionType}): it was tampered with or " +
                    "protected under a Data Protection key ring this host does not have. Re-enter the secret.",
                    siteId, referenceKey, ex.GetType().Name);
                return SiteSecretResult.Unreadable;
            }
        }

        public async Task DeleteAsync(int siteId, string referenceKey, CancellationToken ct = default)
        {
            EnsureValid(referenceKey);
            ct.ThrowIfCancellationRequested();

            var setting = await _settings.GetSettingAsync(EntityNames.Site, siteId, referenceKey);
            if (setting is not null)
            {
                await _settings.DeleteSettingAsync(EntityNames.Site, setting.SettingId);
            }
        }

        private static void EnsureValid(string referenceKey)
        {
            if (!IsValidReferenceKey(referenceKey))
            {
                throw new ArgumentException(
                    $"'{referenceKey}' is not a secret reference key: lower-case dotted segments of [a-z0-9-] (at least two), " +
                    $"at most {MaxReferenceKeyLength} characters - e.g. 'notifications.channel.{{id:N}}.credential'.",
                    nameof(referenceKey));
            }
        }
    }
}