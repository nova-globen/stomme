using System;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Stomme.Shared;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// The site's visitor cookie (<c>APP_VISITOR_{siteId}</c>): the visitor's id and the time the visit record is next
    /// refreshed, PROTECTED with the host's Data Protection - the key ring the authentication and antiforgery cookies use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BL-185. The cookie used to be the id and a date in plain text, and three doors trusted it: the
    /// Visitor branch of the settings API (read and write a visitor's settings), <c>GET api/Visitor/{id}</c> (a visitor's
    /// record - its IP address, user agent, pages and user id) and the visit tracking in <c>App.razor</c>. A caller with no
    /// account who wrote <c>APP_VISITOR_1=123</c> themselves passed as visitor 123, and the ids are sequential.
    /// </para>
    /// <para>
    /// A value that does not unprotect - forged, tampered, made for another purpose, or a legacy plain cookie - reads as
    /// NO visitor. The tracking then treats the browser as a new visitor (or finds it again by IP address when the site
    /// correlates visitors) and issues a protected cookie, so a legacy cookie is replaced on the next page view.
    /// </para>
    /// </remarks>
    public sealed class VisitorCookie
    {
        /// <summary>The Data Protection purpose; a new format takes a new purpose rather than reading the old one.</summary>
        internal const string Purpose = "Stomme.VisitorCookie.v1";

        private readonly IDataProtector _protector;

        public VisitorCookie(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector(Purpose);
        }

        /// <summary>The cookie's name for a site.</summary>
        public static string Name(int siteId) => Constants.VisitorCookiePrefix + siteId.ToString(CultureInfo.InvariantCulture);

        /// <summary>The protected value for a visitor whose record is next refreshed at <paramref name="refreshAtUtc"/>.</summary>
        public string Protect(int visitorId, DateTime refreshAtUtc) =>
            _protector.Protect(visitorId.ToString(CultureInfo.InvariantCulture) + "|" + refreshAtUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// The visitor id and refresh time a value carries, or <c>(-1, DateTime.MinValue)</c> when it carries none that this
        /// host issued.
        /// </summary>
        public (int VisitorId, DateTime RefreshAtUtc) Read(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return (-1, DateTime.MinValue);
            }

            string plain;
            try
            {
                plain = _protector.Unprotect(value);
            }
            catch (CryptographicException)
            {
                return (-1, DateTime.MinValue);
            }

            var parts = plain.Split('|');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var visitorId) && visitorId > 0
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
            {
                return (visitorId, new DateTime(ticks, DateTimeKind.Utc));
            }

            return (-1, DateTime.MinValue);
        }

        /// <summary>The visitor id the request's cookie for <paramref name="siteId"/> proves, or -1.</summary>
        public int VisitorIdOf(HttpRequest request, int siteId) => Read(request.Cookies[Name(siteId)]).VisitorId;
    }
}
