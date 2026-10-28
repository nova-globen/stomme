using System.Linq;
using System.Security.Claims;
using Stomme.Shared;

namespace Stomme.Extensions;

public static class ClaimsPrincipalExtensions
{
    // extension methods cannot be properties - the methods below must include a () suffix when referenced

    public static bool IsAuthenticated(this ClaimsPrincipal claimsPrincipal)
    {
        if (claimsPrincipal.Identity != null)
        {
            return claimsPrincipal.Identity.IsAuthenticated;
        }
        else
        {
            return false;
        }
    }

    public static string Username(this ClaimsPrincipal claimsPrincipal)
    {
        if (claimsPrincipal.HasClaim(item => item.Type == ClaimTypes.Name))
        {
            return claimsPrincipal.Claims.FirstOrDefault(item => item.Type == ClaimTypes.Name).Value;
        }
        else
        {
            return "";
        }
    }

    /// <summary>The principal's Stomme user id, or -1 when it carries none or one that is not a Stomme user id.</summary>
    /// <remarks>
    /// <b><c>TryParse</c>, not <c>Parse</c> (BL-80, D101 item 3).</b> A NameIdentifier that is not an integer is not a
    /// malformed request - it is a principal from another issuer. Stomme's own per-tenant OpenIddict authority
    /// issues client-credentials tokens whose <c>sub</c> is the CLIENT ID, a string; a SAML or Negotiate principal
    /// carries whatever its provider put there. <c>Parse</c> made every one of those an unhandled
    /// <c>FormatException</c> - an empty HTTP 500 that says the endpoint exists and broke, where the honest answer is
    /// "this is not a Stomme user", which -1 already means and every caller already handles.
    /// </remarks>
    public static int UserId(this ClaimsPrincipal claimsPrincipal)
    {
        var claim = claimsPrincipal?.Claims?.FirstOrDefault(item => item.Type == ClaimTypes.NameIdentifier);
        return claim is not null && int.TryParse(claim.Value, out var userId) ? userId : -1;
    }

    public static string[] Roles(this ClaimsPrincipal claimsPrincipal)
    {
        return claimsPrincipal.Claims.Where(item => item.Type == ClaimTypes.Role)
            .Select(item => item.Value).ToArray();
    }

    public static string SiteKey(this ClaimsPrincipal claimsPrincipal)
    {
        if (claimsPrincipal.HasClaim(item => item.Type == Constants.SiteKeyClaimType))
        {
            return claimsPrincipal.Claims.FirstOrDefault(item => item.Type == Constants.SiteKeyClaimType).Value;
        }
        else
        {
            return "";
        }
    }

    public static int TenantId(this ClaimsPrincipal claimsPrincipal)
    {
        var sitekey = SiteKey(claimsPrincipal);
        if (!string.IsNullOrEmpty(sitekey) && sitekey.Contains(":"))
        {
            return int.Parse(sitekey.Split(':')[0]);
        }
        return -1;
    }

    public static int SiteId(this ClaimsPrincipal claimsPrincipal)
    {
        var sitekey = SiteKey(claimsPrincipal);
        if (!string.IsNullOrEmpty(sitekey) && sitekey.Contains(":"))
        {
            return int.Parse(sitekey.Split(':')[1]);
        }
        return -1;
    }

    /// <summary>The server-side session this principal was signed in under (BL-119), or null when it carries none.</summary>
    public static System.Guid? SessionId(this ClaimsPrincipal claimsPrincipal)
    {
        var claim = claimsPrincipal?.Claims?.FirstOrDefault(item => item.Type == Constants.SessionClaimType);
        return claim is not null && System.Guid.TryParse(claim.Value, out var id) ? id : null;
    }

    public static string SecurityStamp(this ClaimsPrincipal claimsPrincipal)
    {
        if (claimsPrincipal.HasClaim(item => item.Type == Constants.SecurityStampClaimType))
        {
            return claimsPrincipal.Claims.FirstOrDefault(item => item.Type == Constants.SecurityStampClaimType).Value;
        }
        else
        {
            return "";
        }
    }

    public static bool IsOnlyInRole(this ClaimsPrincipal claimsPrincipal, string role)
    {
        var identity = claimsPrincipal.Identities.FirstOrDefault(item => item.AuthenticationType == Constants.AuthenticationScheme);
        if (identity != null)
        {
            // check if user has role claim specified and no other role claims
            return identity.Claims.Any(item => item.Type == ClaimTypes.Role && item.Value == role) &&
                !identity.Claims.Any(item => item.Type == ClaimTypes.Role && item.Value != role);
        }
        return false;
    }
}
