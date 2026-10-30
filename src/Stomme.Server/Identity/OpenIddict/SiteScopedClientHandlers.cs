using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Stomme.Extensions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// Keeps a client registered for named sites (<c>OpenIddict:Applications:n:Sites</c>) to those sites: its authorization,
    /// device authorization, token and revocation requests at any other site are refused with <c>unauthorized_client</c>. The OpenIddict store is
    /// per TENANT and several sites can share a tenant, so the store alone would accept the client at every one of them.
    /// A client with no sites is accepted at every site of its tenant, as before.
    /// </summary>
    internal static class SiteScopedClientHandlers
    {
        /// <summary>The application property holding the site aliases, a JSON array of lower-case alias names.</summary>
        public const string SitesProperty = "stomme_sites";

        public static void Register(OpenIddictServerBuilder options)
        {
            // After OpenIddict's own checks, so the client is known to exist; a rejection anywhere stops the request.
            const int order = int.MaxValue - 100_000;
            options.AddEventHandler<ValidateAuthorizationRequestContext>(builder => builder.UseScopedHandler<Authorization>().SetOrder(order).SetType(OpenIddictServerHandlerType.Custom));
            options.AddEventHandler<ValidateTokenRequestContext>(builder => builder.UseScopedHandler<Token>().SetOrder(order).SetType(OpenIddictServerHandlerType.Custom));
            options.AddEventHandler<ValidateRevocationRequestContext>(builder => builder.UseScopedHandler<Revocation>().SetOrder(order).SetType(OpenIddictServerHandlerType.Custom));
            options.AddEventHandler<ValidateDeviceAuthorizationRequestContext>(builder => builder.UseScopedHandler<DeviceAuthorization>().SetOrder(order).SetType(OpenIddictServerHandlerType.Custom));
        }

        /// <summary>An alias as the property stores it: trimmed, without a trailing slash, lower case.</summary>
        public static string Normalize(string alias) => (alias ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

        /// <summary>The site aliases a client is limited to, empty for a client limited to none.</summary>
        public static IReadOnlyList<string> ReadSites(ImmutableDictionary<string, JsonElement> properties)
        {
            if (properties == null || !properties.TryGetValue(SitesProperty, out var value) || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => Normalize(item.GetString()))
                .Where(item => item.Length > 0)
                .ToList();
        }

        private static async ValueTask CheckAsync(BaseValidatingContext context, string clientId, IOpenIddictApplicationManager applications)
        {
            if (string.IsNullOrEmpty(clientId))
            {
                return;
            }

            var application = await applications.FindByClientIdAsync(clientId);
            if (application is null)
            {
                return;
            }

            var sites = ReadSites(await applications.GetPropertiesAsync(application));
            if (sites.Count == 0)
            {
                return;
            }

            var alias = context.Transaction.GetHttpRequest()?.HttpContext.GetAlias();
            if (alias == null || !sites.Contains(Normalize(alias.Name)))
            {
                context.Reject(Errors.UnauthorizedClient, "This client is not registered for this site.");
            }
        }

        internal sealed class Authorization(IOpenIddictApplicationManager applications) : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
        {
            public ValueTask HandleAsync(ValidateAuthorizationRequestContext context) => CheckAsync(context, context.ClientId, applications);
        }

        internal sealed class Token(IOpenIddictApplicationManager applications) : IOpenIddictServerHandler<ValidateTokenRequestContext>
        {
            public ValueTask HandleAsync(ValidateTokenRequestContext context) => CheckAsync(context, context.ClientId, applications);
        }

        internal sealed class Revocation(IOpenIddictApplicationManager applications) : IOpenIddictServerHandler<ValidateRevocationRequestContext>
        {
            public ValueTask HandleAsync(ValidateRevocationRequestContext context) => CheckAsync(context, context.ClientId, applications);
        }

        internal sealed class DeviceAuthorization(IOpenIddictApplicationManager applications) : IOpenIddictServerHandler<ValidateDeviceAuthorizationRequestContext>
        {
            public ValueTask HandleAsync(ValidateDeviceAuthorizationRequestContext context) => CheckAsync(context, context.ClientId, applications);
        }
    }
}
