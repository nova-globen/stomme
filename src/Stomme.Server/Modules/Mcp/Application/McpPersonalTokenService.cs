#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp.Persistence;
using Stomme.Models.Mcp;
using Stomme.Modules.Mcp.Domain;
using Stomme.Security.Abstractions;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Modules.Mcp.Application
{
    /// <summary>
    /// The personal MCP tokens (D101a, BL-101): a signed-in user makes, lists and revokes their OWN tokens on the site the
    /// request resolved; <see cref="ResolveAsync"/> finds the token a client sent, for <see cref="McpPersonalTokenMiddleware"/>.
    /// Every by-id call names the caller's site and user, so another user's token answers as no token.
    /// </summary>
    public sealed class McpPersonalTokenService(
        IMcpPersonalTokenRepository tokens,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        IMcpClock clock)
    {
        // How often a token's "last used" is written: at most once a minute, so a busy client does not write on every request.
        private static readonly TimeSpan UsedResolution = TimeSpan.FromMinutes(1);

        public async Task<IReadOnlyList<McpTokenView>> ListAsync(CancellationToken ct = default)
        {
            var (siteId, userId) = Caller();
            return (await tokens.ListForUserAsync(siteId, userId, ct)).OrderByDescending(token => token.CreatedOnUtc).Select(ToView).ToList();
        }

        /// <exception cref="ArgumentException">A name, a number of days or a scope the token cannot have; the message says which.</exception>
        public async Task<McpTokenCreated> CreateAsync(McpTokenRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var (siteId, userId) = Caller();
            var tenantId = tenantContext.CurrentTenantId ?? throw new UnauthorizedAccessException("A token is made on a site.");
            var (token, secret) = McpPersonalTokenAggregate.Issue(Guid.NewGuid(), tenantId, siteId, userId, request.Name, request.Days, request.Scopes ?? Array.Empty<string>(), clock.UtcNow);
            await tokens.AddAsync(token, ct);
            return new McpTokenCreated { Token = ToView(token), Secret = secret };
        }

        /// <summary>Revokes one of the caller's own tokens at once; false when they have no such token.</summary>
        public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
        {
            var (siteId, userId) = Caller();
            var token = await tokens.GetForUserAsync(id, siteId, userId, ct);
            if (token is null)
            {
                return false;
            }

            token.Revoke(clock.UtcNow);
            await tokens.UpdateAsync(token, ct);
            return true;
        }

        /// <summary>
        /// The token <paramref name="secret"/> is, when it may sign its user in on <paramref name="siteId"/> now: one this site
        /// issued, not revoked, not expired. Null otherwise - one answer for every reason, so a caller learns nothing.
        /// </summary>
        public async Task<McpPersonalTokenAggregate?> ResolveAsync(string secret, int siteId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(secret) || !secret.StartsWith(McpPersonalTokenAggregate.SecretPrefix, StringComparison.Ordinal) || siteId <= 0)
            {
                return null;
            }

            var token = await tokens.GetBySecretHashAsync(McpPersonalTokenAggregate.Hash(secret), ct);
            var now = clock.UtcNow;
            if (token is null || token.SiteId != siteId || !token.IsUsable(now))
            {
                return null;
            }

            if (token.LastUsedOnUtc is not { } used || now - used >= UsedResolution)
            {
                token.MarkUsed(now);
                await tokens.UpdateAsync(token, ct);
            }

            return token;
        }

        public static McpTokenView ToView(McpPersonalTokenAggregate token) => new()
        {
            Id = token.Id,
            Name = token.Name,
            Prefix = token.Prefix,
            Scopes = token.ScopeList(),
            CreatedOnUtc = token.CreatedOnUtc,
            ExpiresOnUtc = token.ExpiresOnUtc,
            LastUsedOnUtc = token.LastUsedOnUtc,
            RevokedOnUtc = token.RevokedOnUtc,
        };

        private (int SiteId, int UserId) Caller() =>
            currentUser.UserId is { } userId && userId > 0 && currentUser.SiteId > 0
                ? (currentUser.SiteId, userId)
                : throw new UnauthorizedAccessException("Personal MCP tokens are a signed-in user's own.");
    }
}
