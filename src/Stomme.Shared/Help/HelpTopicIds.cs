#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace Stomme.Help
{
    /// <summary>
    /// The DETERMINISTIC id of a help topic: one <see cref="Guid"/> per (tenant, site, anchor, locale), the same on every
    /// provider, in every process, for ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes the reconciler safe to run on every boot.</b> Consider the alternative:
    /// a reconciler that looks a topic up by its fields and creates it when it does not find one will create it again
    /// on the next boot the moment the look-up is wrong - and there is no unique index on ANY provider
    /// (<c>EnsureCreated</c>, properties only), so the rows simply accumulate. With a deterministic id the reconciler
    /// is a GetById-then-Add-or-Update, and running it twice is running it once.
    /// </para>
    /// <para>
    /// <b>It also removes a query that would be unsafe.</b> Resolving a topic by
    /// <c>FindAsync(t =&gt; t.Anchor == a &amp;&amp; t.Locale == l)</c> is a string comparison, and this repository has
    /// measured three different answers to that across its providers: case-sensitive on the relational ones,
    /// case-insensitive on RavenDB. A by-id read has one answer everywhere.
    /// </para>
    /// <para>
    /// <b>The construction is a name-based UUID</b> (RFC 4122 §4.3, the version-5 shape with SHA-256 truncated rather
    /// than SHA-1) over a NAMESPACE and a canonical name. The bytes are written big-endian and the version and
    /// variant nibbles are set explicitly, so the value does not depend on <see cref="Guid"/>'s little-endian layout -
    /// the trap that makes a naive <c>new Guid(hash)</c> differ between platforms.
    /// </para>
    /// </remarks>
    public static class HelpTopicIds
    {
        /// <summary>
        /// The namespace these ids are generated under. Changing it re-keys every topic in every install, which is a
        /// migration and not an edit.
        /// </summary>
        public static readonly Guid Namespace = new("6f1f4e2a-3c7b-4a9e-9d51-0b7a2c5e8d14");

        /// <summary>
        /// The id for a topic. <paramref name="anchor"/> and <paramref name="locale"/> are normalized here as well as
        /// by their callers: the id must not depend on whether the caller remembered to.
        /// </summary>
        /// <remarks>
        /// <b>The TENANT is part of the key.</b> Site ids are NOT
        /// unique across tenants: every tenant database numbers its sites from 1, two tenants may share one database
        /// (site-per-alias), and the module store is ONE store for the whole install. Keyed on (site, anchor, locale)
        /// alone, tenant B's topic for site 1 had tenant A's primary key - on EF the insert collides, and because the
        /// tenant filter HIDES tenant A's row, tenant B's GetById-then-Add reads a miss and fails again on every boot.
        /// Metering shipped exactly this (<c>MeteringTenantIdCollisionTests</c>). It takes the tenant's <see cref="Guid"/>
        /// rather than the domain type because this seam names nothing outside <c>Stomme.Shared</c>.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="tenantId"/> is empty, or the anchor or locale cannot be normalized.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="siteId"/> is not a site.</exception>
        public static Guid For(Guid tenantId, int siteId, string anchor, string locale)
        {
            if (tenantId == Guid.Empty)
            {
                throw new ArgumentException("A help topic belongs to a tenant.", nameof(tenantId));
            }

            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A help topic belongs to a site.");
            }

            if (!HelpKeys.TryNormalizeAnchor(anchor, out var normalizedAnchor))
            {
                throw new ArgumentException($"'{anchor}' is not a usable help anchor.", nameof(anchor));
            }

            if (!HelpKeys.TryNormalizeLocale(locale, out var normalizedLocale))
            {
                throw new ArgumentException($"'{locale}' is not a usable locale tag.", nameof(locale));
            }

            // The separator is a character none of the four parts can contain, so ("a|b", "c") and ("a", "b|c") cannot
            // collide.
            return NameBased(Namespace, tenantId.ToString("N") + "|" + siteId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + normalizedAnchor + "|" + normalizedLocale);
        }

        /// <summary>The RFC 4122 §4.3 construction, with SHA-256 in place of SHA-1. See the type remarks.</summary>
        private static Guid NameBased(Guid namespaceId, string name)
        {
            var namespaceBytes = namespaceId.ToByteArray();
            SwapToBigEndian(namespaceBytes);

            var nameBytes = Encoding.UTF8.GetBytes(name);
            var buffer = new byte[namespaceBytes.Length + nameBytes.Length];
            Buffer.BlockCopy(namespaceBytes, 0, buffer, 0, namespaceBytes.Length);
            Buffer.BlockCopy(nameBytes, 0, buffer, namespaceBytes.Length, nameBytes.Length);

            var hash = SHA256.HashData(buffer);
            var id = new byte[16];
            Buffer.BlockCopy(hash, 0, id, 0, 16);

            id[6] = (byte)((id[6] & 0x0F) | 0x50);      // version 5
            id[8] = (byte)((id[8] & 0x3F) | 0x80);      // RFC 4122 variant

            SwapToBigEndian(id);
            return new Guid(id);
        }

        /// <summary>
        /// Swaps the first three fields between <see cref="Guid"/>'s little-endian layout and the RFC's big-endian
        /// one. Its own inverse, which is why one method serves both directions.
        /// </summary>
        private static void SwapToBigEndian(byte[] bytes)
        {
            (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
            (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
            (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
            (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
        }
    }
}
