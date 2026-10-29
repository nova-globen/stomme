#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// The webhook receiver's security (BL-95 increment (e), the predecessor platform's webhook trigger re-homed): every request carries
    /// <c>X-Signature-256: sha256=&lt;hex HMAC-SHA256 of the raw body&gt;</c> under the site's configured secret, compared
    /// in constant time; a request without a valid signature is 401 with no detail; the receiver is rate-limited per
    /// site and caller address with the host's own rate limiter (the policy below, the same shape as the MCP server's);
    /// and it answers 202 whether or not a pipeline listens under the name, so an outsider cannot enumerate the site's
    /// webhooks. the predecessor platform's per-endpoint IP allowlist and its deduplication window arrived 2026-09-28 as configuration beside the
    /// secret (<see cref="IsCallerAllowed"/>, <see cref="IsFirstDeliveryAsync"/>); its expression-keyed deduplication is not carried.
    /// </summary>
    public static class PipelineWebhooks
    {
        public const string SignatureHeaderName = "X-Signature-256";
        public const string SignaturePrefix = "sha256=";
        public const string PolicyName = "pipelines-webhooks";

        public static string ComputeSignature(string secret, ReadOnlySpan<byte> payload)
        {
            Span<byte> hash = stackalloc byte[32];
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload, hash);
            return SignaturePrefix + Convert.ToHexStringLower(hash);
        }

        public static bool VerifySignature(string? secret, ReadOnlySpan<byte> payload, string? providedSignature)
        {
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(providedSignature))
            {
                return false;
            }

            var expected = Encoding.ASCII.GetBytes(ComputeSignature(secret, payload));
            var provided = Encoding.ASCII.GetBytes(Normalize(providedSignature));
            return CryptographicOperations.FixedTimeEquals(expected, provided);
        }

        private static string Normalize(string signature)
        {
            var trimmed = signature.Trim();
            return trimmed.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase)
                ? SignaturePrefix + trimmed[SignaturePrefix.Length..].ToLowerInvariant()
                : SignaturePrefix + trimmed.ToLowerInvariant();
        }

        /// <summary>
        /// the predecessor platform's rule for a webhook's allowlist: empty admits any caller; otherwise the caller's address must equal an entry
        /// (parsed, so equivalent spellings match; an IPv4-mapped IPv6 address is its IPv4 address) or fall in an entry's CIDR
        /// range. No known address (none, or one that does not parse) is refused by a non-empty list.
        /// </summary>
        public static bool IsCallerAllowed(IReadOnlyList<string> allowlist, IPAddress? caller)
        {
            if (allowlist is null || allowlist.Count == 0)
            {
                return true;
            }

            if (caller is null)
            {
                return false;
            }

            var address = caller.IsIPv4MappedToIPv6 ? caller.MapToIPv4() : caller;
            foreach (var entry in allowlist)
            {
                var text = entry?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (text.Contains('/', StringComparison.Ordinal))
                {
                    if (IPNetwork.TryParse(text, out var network) && network.Contains(address))
                    {
                        return true;
                    }
                }
                else if (IPAddress.TryParse(text, out var literal) && (literal.IsIPv4MappedToIPv6 ? literal.MapToIPv4() : literal).Equals(address))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True the first time an authentic delivery with this key reaches the site's webhook within <paramref name="window"/>;
        /// false for a repeat. The mark is the framework cache's get-or-set - the first caller's token is what is stored, and the
        /// cache runs one factory per key on a node - so two repeats racing on one node both see the first's token; across
        /// nodes it holds where the cache has its distributed tier. The key is hashed: a header's value is the caller's text.
        /// </summary>
        public static async Task<bool> IsFirstDeliveryAsync(ICacheManager cache, Alias alias, string name, string material, TimeSpan window)
        {
            var key = "pipelines:webhook:" + name + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
            var token = Guid.NewGuid();
            var stored = await cache.GetCacheAsync(alias, key, _ => Task.FromResult(token), window, window).ConfigureAwait(false);
            return stored == token;
        }

        /// <summary>
        /// The receiver's rate-limit policy: a fixed window per site and caller address. The host owns the 429 answer. The forms'
        /// pages take one of their own under another name, so a busy webhook never starves a form, nor a form a webhook.
        /// </summary>
        public static void AddPolicy(IServiceCollection services, int requestsPerMinute, string policyName = PolicyName)
        {
            if (requestsPerMinute <= 0)
            {
                // No limit - but a policy by the name all the same: the endpoints name it, and a named policy that does not
                // exist fails their every request.
                services.AddRateLimiter(options => options.AddPolicy(policyName, _ => RateLimitPartition.GetNoLimiter(policyName)));
                return;
            }

            services.AddRateLimiter(options =>
            {
                options.AddPolicy(policyName, context => RateLimitPartition.GetFixedWindowLimiter(
                    policyName + "|" + PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = requestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    }));
            });
        }

        private static string PartitionKey(HttpContext context)
        {
            var site = context.RequestServices.GetService<SiteState>()?.Alias?.SiteKey ?? context.Request.Host.Host;
            var caller = context.User?.Identity?.IsAuthenticated == true
                ? "u:" + (context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.Identity.Name ?? "?")
                : "a:" + (context.Connection.RemoteIpAddress?.ToString() ?? "?");
            return "webhook|" + site + "|" + caller;
        }
    }
}
