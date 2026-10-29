using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// OpenIddict's application manager with RFC 8252 7.3: a native app that receives its redirect on a loopback IP address
    /// (<c>http://127.0.0.1</c> or <c>http://[::1]</c>) picks the port when it starts, so a request for such a redirect matches a
    /// registered one that differs only in the port. Everything else is OpenIddict's exact comparison.
    /// </summary>
    public sealed class LoopbackRedirectApplicationManager<TApplication> : OpenIddictApplicationManager<TApplication>
        where TApplication : class
    {
        public LoopbackRedirectApplicationManager(
            IOpenIddictApplicationCache<TApplication> cache,
            ILogger<OpenIddictApplicationManager<TApplication>> logger,
            IOptionsMonitor<OpenIddictCoreOptions> options,
            IOpenIddictApplicationStore<TApplication> store)
            : base(cache, logger, options, store)
        {
        }

        public override async ValueTask<bool> ValidateRedirectUriAsync(TApplication application, string uri, CancellationToken cancellationToken = default)
        {
            if (await base.ValidateRedirectUriAsync(application, uri, cancellationToken))
            {
                return true;
            }

            if (!IsLoopback(uri, out var requested))
            {
                return false;
            }

            foreach (var registered in await GetRedirectUrisAsync(application, cancellationToken))
            {
                if (IsLoopback(registered, out var candidate) && SameExceptPort(requested, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>An <c>http</c> redirect to a loopback IP literal - not the name <c>localhost</c>, which RFC 8252 advises against.</summary>
        internal static bool IsLoopback(string uri, out Uri parsed)
        {
            return Stomme.Shared.Utilities.TryCreateAbsoluteUri(uri, out parsed)
                && parsed.Scheme == Uri.UriSchemeHttp
                && parsed.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
                && parsed.IsLoopback
                && string.IsNullOrEmpty(parsed.UserInfo)
                && string.IsNullOrEmpty(parsed.Fragment);
        }

        internal static bool SameExceptPort(Uri requested, Uri registered) =>
            string.Equals(requested.Host, registered.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(requested.AbsolutePath, registered.AbsolutePath, StringComparison.Ordinal)
            && string.Equals(requested.Query, registered.Query, StringComparison.Ordinal);
    }
}
