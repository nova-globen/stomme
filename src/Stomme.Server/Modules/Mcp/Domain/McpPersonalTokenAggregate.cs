#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Stomme.Models.Mcp;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Mcp.Domain
{
    /// <summary>
    /// A personal MCP token (D101a, BL-101): what lets an MCP client outside the browser - Claude Desktop, an agent on another
    /// machine - reach this site's <c>/mcp/v1</c> as ONE user, on ONE site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only its hash is kept.</b> The secret (<c>smcp_</c> and 43 characters of 32 random bytes) is shown once, when the
    /// token is made; what is stored is its SHA-256 and its first characters, so neither the database nor a backup holds a
    /// working token. A lookup hashes what the client sent and asks for that hash.
    /// </para>
    /// <para>
    /// <b>Bound to its user and its site</b>, it authenticates nowhere else; <b>its scopes only narrow</b> what that user may
    /// already do (the permission rows decide every resource and tool, as for the in-browser assistant); <b>it always
    /// expires</b> (1 to 365 days); and once revoked, expired or its user deleted it is refused.
    /// </para>
    /// </remarks>
    public sealed class McpPersonalTokenAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const string SecretPrefix = "smcp_";
        public const int MaximumNameLength = 100;
        public const int MaximumDays = 365;

        private DateTime _createdOnUtc;
        private DateTime _expiresOnUtc;
        private DateTime? _lastUsedOnUtc;
        private DateTime? _revokedOnUtc;

        // Materialisation constructor. Not for application use.
        private McpPersonalTokenAggregate()
        {
            TenantId = null!;
            Name = string.Empty;
            Prefix = string.Empty;
            SecretHash = string.Empty;
            Scopes = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public int UserId { get; private set; }

        public string Name { get; private set; }

        /// <summary>The secret's first characters, shown in lists to tell tokens apart.</summary>
        public string Prefix { get; private set; }

        /// <summary>The secret's SHA-256, lower-case hex: what a request's token is looked up by.</summary>
        public string SecretHash { get; private set; }

        /// <summary>The scopes, comma-separated (<see cref="McpTokenScopes"/>).</summary>
        public string Scopes { get; private set; }

        public DateTime CreatedOnUtc
        {
            get => McpInstant.Stamp(_createdOnUtc);
            private set => _createdOnUtc = value;
        }

        public DateTime ExpiresOnUtc
        {
            get => McpInstant.Stamp(_expiresOnUtc);
            private set => _expiresOnUtc = value;
        }

        public DateTime? LastUsedOnUtc
        {
            get => _lastUsedOnUtc is { } used ? McpInstant.Stamp(used) : null;
            private set => _lastUsedOnUtc = value;
        }

        public DateTime? RevokedOnUtc
        {
            get => _revokedOnUtc is { } revoked ? McpInstant.Stamp(revoked) : null;
            private set => _revokedOnUtc = value;
        }

        // A method, not a property: a store that writes every public property would keep it as a second copy of Scopes.
        public IReadOnlyList<string> ScopeList() => Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Makes a token and its secret. The secret is returned to be shown once; the token keeps only its hash.</summary>
        public static (McpPersonalTokenAggregate Token, string Secret) Issue(
            Guid id, TenantId tenantId, int siteId, int userId, string name, int days, IEnumerable<string> scopes, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (id == Guid.Empty)
            {
                throw new ArgumentException("A token needs an id.", nameof(id));
            }

            if (siteId <= 0 || userId <= 0)
            {
                throw new ArgumentOutOfRangeException(siteId <= 0 ? nameof(siteId) : nameof(userId), "A token belongs to one user on one site.");
            }

            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaximumNameLength)
            {
                throw new ArgumentException($"A token needs a name of 1 to {MaximumNameLength} characters, to tell it apart later.", nameof(name));
            }

            if (days < 1 || days > MaximumDays)
            {
                throw new ArgumentException($"A token lives 1 to {MaximumDays} days.", nameof(days));
            }

            var chosen = (scopes ?? Array.Empty<string>()).Select(scope => (scope ?? string.Empty).Trim().ToLowerInvariant()).Distinct().ToList();
            if (chosen.Count == 0 || chosen.Any(scope => !McpTokenScopes.All.Contains(scope)))
            {
                throw new ArgumentException($"A token may do {string.Join(" and/or ", McpTokenScopes.All)}, and at least one.", nameof(scopes));
            }

            var now = McpInstant.Require(nowUtc, nameof(nowUtc));
            var secret = SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var token = new McpPersonalTokenAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                UserId = userId,
                Name = trimmed,
                Prefix = secret.Substring(0, SecretPrefix.Length + 8),
                SecretHash = Hash(secret),
                Scopes = string.Join(",", McpTokenScopes.All.Where(chosen.Contains)),
                CreatedOnUtc = now,
                ExpiresOnUtc = now.AddDays(days),
            };
            return (token, secret);
        }

        /// <summary>The hash a secret is stored and looked up by.</summary>
        public static string Hash(string secret) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret ?? string.Empty))).ToLowerInvariant();

        /// <summary>Whether it may sign its user in now: not revoked and not expired.</summary>
        public bool IsUsable(DateTime nowUtc) => RevokedOnUtc is null && McpInstant.Require(nowUtc, nameof(nowUtc)) < ExpiresOnUtc;

        public void Revoke(DateTime nowUtc) => RevokedOnUtc ??= McpInstant.Require(nowUtc, nameof(nowUtc));

        public void MarkUsed(DateTime nowUtc) => LastUsedOnUtc = McpInstant.Require(nowUtc, nameof(nowUtc));
    }
}
