using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using Stomme.Extensions;
using Stomme.Infrastructure.Security;
using Stomme.Shared;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// The device authorization grant (RFC 8628, D117): a device without a browser - a TV - asks <c>connect/device</c> for a
    /// device code and a user code, the person opens <c>connect/verify</c> on their phone or computer, signs in with the site's
    /// normal login, checks which app asks on which site and approves or denies, and the device's polling of
    /// <c>connect/token</c> then returns tokens. Opt-in per client (the <c>device_code</c> grant in its configuration). A code
    /// belongs to the SITE that issued it: the site is stamped on it when it is made, and verification or redemption at any
    /// other site is refused, even one sharing the tenant's store.
    /// </summary>
    internal static class DeviceAuthorizationEndpoints
    {
        /// <summary>The configuration key for the seconds a device waits between polls (RFC 8628 3.5; default 5).</summary>
        public const string PollingIntervalKey = "OpenIddict:DevicePollingIntervalSeconds";

        // When each device code was last polled, by its hash; entries older than a code's lifetime are dropped. One per
        // process: enough to slow a device that polls too fast, which is all slow_down is for.
        private static readonly ConcurrentDictionary<string, DateTimeOffset> LastPoll = new(StringComparer.Ordinal);

        /// <summary>Wires the endpoints, the grant, the code lifetimes, polling and the site stamp into the server.</summary>
        public static void Configure(OpenIddictServerBuilder options, IConfiguration configuration)
        {
            var interval = int.TryParse(configuration?[PollingIntervalKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds
                : 5;

            // RELATIVE, like the other endpoints, so a folder site's discovery names /<folder>/connect/device.
            options.SetDeviceAuthorizationEndpointUris("connect/device");
            options.SetEndUserVerificationEndpointUris("connect/verify");
            options.AllowDeviceAuthorizationFlow();

            // A user code is typed on another device within minutes; OpenIddict's default user code is digits only, which
            // reads unambiguously off a TV and has no case to get wrong.
            options.SetDeviceCodeLifetime(TimeSpan.FromMinutes(10));
            options.SetUserCodeLifetime(TimeSpan.FromMinutes(10));

            // The device is told how long to wait between polls, and one that polls sooner is told to slow down (OpenIddict
            // answers authorization_pending however fast it is asked). Before OpenIddict's own checks, so a pending code
            // polled too soon gets slow_down.
            options.AddEventHandler<ApplyDeviceAuthorizationResponseContext>(builder => builder
                .UseInlineHandler(context =>
                {
                    if (string.IsNullOrEmpty(context.Error) && context.Response != null)
                    {
                        context.Response["interval"] = interval;
                    }

                    return default;
                })
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom));
            options.AddEventHandler<ValidateTokenRequestContext>(builder => builder
                .UseInlineHandler(context =>
                {
                    if (context.Request.IsDeviceCodeGrantType() && !string.IsNullOrEmpty(context.Request.DeviceCode) && TooSoon(context.Request.DeviceCode, interval))
                    {
                        context.Reject(Errors.SlowDown, $"Poll at most every {interval} seconds.");
                    }

                    return default;
                })
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom));

            // The issuing site rides on the device and user codes, early, before OpenIddict turns the principal into them.
            options.AddEventHandler<ProcessSignInContext>(builder => builder
                .UseInlineHandler(context =>
                {
                    if (context.EndpointType == OpenIddictServerEndpointType.DeviceAuthorization && context.Principal?.Identity is ClaimsIdentity identity)
                    {
                        identity.SetClaim(StommeOpenIddictExtensions.SiteClaim, context.Transaction.GetHttpRequest()?.HttpContext.GetAlias()?.Name);
                    }

                    return default;
                })
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom));
        }

        /// <summary>True when this device code was polled less than the interval ago; records this poll either way.</summary>
        private static bool TooSoon(string deviceCode, int interval)
        {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(deviceCode)));
            var now = DateTimeOffset.UtcNow;
            var soon = LastPoll.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(interval);
            LastPoll[key] = now;

            foreach (var stale in LastPoll.Where(entry => now - entry.Value > TimeSpan.FromMinutes(15)).Select(entry => entry.Key).ToList())
            {
                LastPoll.TryRemove(stale, out _);
            }

            return soon;
        }

        /// <summary>The site a device or user code was issued at.</summary>
        public static string IssuedAt(ClaimsPrincipal principal) => principal?.GetClaim(StommeOpenIddictExtensions.SiteClaim) ?? string.Empty;

        /// <summary>True when the code was issued at the site the request is for.</summary>
        public static bool IsThisSite(HttpContext context, ClaimsPrincipal principal) =>
            string.Equals(
                SiteScopedClientHandlers.Normalize(IssuedAt(principal)),
                SiteScopedClientHandlers.Normalize(context.GetAlias()?.Name),
                StringComparison.Ordinal) && IssuedAt(principal).Length > 0;

        /// <summary>Maps <c>connect/verify</c>: the page (GET) and the decision (POST, rate-limited as a sign-in attempt).</summary>
        public static void Map(IEndpointRouteBuilder endpoints)
        {
            // Typed as returning the IResult: a lambda returning Task<IResult> also converts to RequestDelegate, which binds the
            // RequestDelegate overload and DISCARDS the result - every answer an empty 200 (measured).
            endpoints.MapGet("/connect/verify", (Func<HttpContext, Task<IResult>>)(context => VerifyAsync(context, decide: false)));
            endpoints.MapPost("/connect/verify", (Func<HttpContext, Task<IResult>>)(context => VerifyAsync(context, decide: true)))
                .RequireRateLimiting(AccountRateLimiting.SignInPolicy);
        }

        private static async Task<IResult> VerifyAsync(HttpContext context, bool decide)
        {
            // The site's normal sign-in, then back here with the code still in the address.
            var signedIn = await context.AuthenticateAsync(Constants.AuthenticationScheme);
            if (signedIn?.Succeeded != true || signedIn.Principal?.Identity?.IsAuthenticated != true)
            {
                // To the site's own login (under its PathBase), and back here with the code still in the address.
                var back = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                return Results.Redirect($"{context.Request.PathBase}/login?returnurl={WebUtility.UrlEncode(back)}");
            }

            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            if (decide && !await antiforgery.IsRequestValidAsync(context))
            {
                return Page(context, antiforgery, "This form has expired. Enter the code again.", code: null, client: null, StatusCodes.Status400BadRequest);
            }

            if (context.Request.Query.ContainsKey("done"))
            {
                return Page(context, antiforgery, context.Request.Query["done"] == "approved"
                    ? "Done. Return to your device; it signs in by itself."
                    : "The request was denied. Nothing was signed in.", code: null, client: null, StatusCodes.Status200OK, finished: true);
            }

            var request = context.GetOpenIddictServerRequest();
            if (string.IsNullOrEmpty(request?.UserCode))
            {
                return Page(context, antiforgery, "Enter the code your device shows.", code: null, client: null, StatusCodes.Status200OK);
            }

            // OpenIddict has validated the user code; an unknown, used or expired one does not authenticate.
            var code = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (code?.Succeeded != true || code.Principal == null)
            {
                return Page(context, antiforgery, "That code is not valid or has expired. Check it on your device and try again.", code: null, client: null, StatusCodes.Status400BadRequest);
            }

            if (!IsThisSite(context, code.Principal))
            {
                return Page(context, antiforgery, "That code belongs to another site. Open the address your device shows.", code: null, client: null, StatusCodes.Status400BadRequest);
            }

            var applications = context.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
            var clientId = code.Principal.GetPresenters().FirstOrDefault() ?? string.Empty;
            var application = string.IsNullOrEmpty(clientId) ? null : await applications.FindByClientIdAsync(clientId);
            var client = application == null ? clientId : (await applications.GetLocalizedDisplayNameAsync(application) ?? clientId);

            if (!decide)
            {
                return Page(context, antiforgery, null, request.UserCode, client, StatusCodes.Status200OK);
            }

            var done = context.Request.PathBase + context.Request.Path + "?done=";
            if (!string.Equals(context.Request.Form["decision"], "approve", StringComparison.Ordinal))
            {
                // The device's next poll gets access_denied.
                return Results.Forbid(
                    new AuthenticationProperties { RedirectUri = done + "denied" },
                    new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
            }

            var principal = await UserPrincipalAsync(context, signedIn.Principal);
            if (principal == null)
            {
                return Page(context, antiforgery, "Your account could not be found on this site.", code: null, client: null, StatusCodes.Status400BadRequest);
            }

            // The same claims and destinations the authorize endpoint gives, the scopes the device asked for, the api resource.
            principal.SetScopes(code.Principal.GetScopes());
            principal.SetResources(StommeOpenIddictExtensions.ApiResource);
            principal.SetDestinations(StommeOpenIddictExtensions.GetDestinations);
            return Results.SignIn(principal, new AuthenticationProperties { RedirectUri = done + "approved" }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        private static async Task<ClaimsPrincipal> UserPrincipalAsync(HttpContext context, ClaimsPrincipal signedIn)
        {
            var userManager = context.RequestServices.GetRequiredService<UserManager<IdentityUser>>();
            var userName = signedIn.Identity?.Name;
            var user = string.IsNullOrEmpty(userName) ? null : await userManager.FindByNameAsync(userName);
            if (user == null)
            {
                return null;
            }

            var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, await userManager.GetUserIdAsync(user));
            identity.SetClaim(Claims.Name, userName);
            identity.SetClaim(Claims.PreferredUsername, userName);
            var email = await userManager.GetEmailAsync(user);
            if (!string.IsNullOrEmpty(email))
            {
                identity.SetClaim(Claims.Email, email);
            }

            identity.SetClaim(StommeOpenIddictExtensions.SiteClaim, context.GetAlias()?.Name);
            return new ClaimsPrincipal(identity);
        }

        /// <summary>The verification page: a message, and either the code form or the approve/deny form.</summary>
        private static IResult Page(HttpContext context, IAntiforgery antiforgery, string message, string code, string client, int status, bool finished = false)
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            var action = WebUtility.HtmlEncode(context.Request.PathBase + context.Request.Path);
            var site = WebUtility.HtmlEncode(context.GetAlias()?.Name ?? context.Request.Host.Value);
            var hidden = $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(tokens.FormFieldName)}\" value=\"{WebUtility.HtmlEncode(tokens.RequestToken)}\" />";

            var body = new StringBuilder();
            body.Append("<main><h1>Sign in a device</h1>");
            if (!string.IsNullOrEmpty(message))
            {
                body.Append("<p role=\"status\">").Append(WebUtility.HtmlEncode(message)).Append("</p>");
            }

            if (finished)
            {
                // Nothing more to do here.
            }
            else if (code == null)
            {
                body.Append($"<form method=\"get\" action=\"{action}\"><label for=\"user_code\">Code</label> ")
                    .Append("<input id=\"user_code\" name=\"user_code\" inputmode=\"numeric\" autocomplete=\"one-time-code\" autofocus required /> ")
                    .Append("<button type=\"submit\">Continue</button></form>");
            }
            else
            {
                body.Append($"<p><strong>{WebUtility.HtmlEncode(client)}</strong> asks to sign in as you on <strong>{site}</strong>.</p>")
                    .Append($"<p>Code: <strong>{WebUtility.HtmlEncode(code)}</strong>. Approve only if your device shows this code.</p>")
                    .Append($"<form method=\"post\" action=\"{action}\">{hidden}")
                    .Append($"<input type=\"hidden\" name=\"user_code\" value=\"{WebUtility.HtmlEncode(code)}\" />")
                    .Append("<button type=\"submit\" name=\"decision\" value=\"approve\">Approve</button> ")
                    .Append("<button type=\"submit\" name=\"decision\" value=\"deny\">Deny</button></form>");
            }

            body.Append("</main>");
            var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\" /><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />" +
                "<title>Sign in a device</title><style>body{font-family:system-ui,sans-serif;margin:0;padding:16px;}main{max-width:32rem;margin:2rem auto;}" +
                "input,button{font-size:1.1rem;padding:.5rem .75rem;margin:.25rem 0;}label{display:block;margin-bottom:.25rem;}</style></head><body>" +
                body + "</body></html>";
            return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
        }
    }
}
