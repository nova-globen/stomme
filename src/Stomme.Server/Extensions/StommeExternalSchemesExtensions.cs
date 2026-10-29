using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sustainsys.Saml2;
using Sustainsys.Saml2.AspNetCore2;
using Sustainsys.Saml2.Metadata;

namespace Stomme.Extensions
{
    /// <summary>
    /// Installation-wide, config-gated external-login CONSUMER schemes: SAML2 (Sustainsys) and
    /// Windows/Negotiate. Both are OFF by default, so a stock install registers neither and is byte-for-byte
    /// unaffected. Neither is ever a default or global challenge: <c>DefaultScheme</c> stays the Identity
    /// cookie (set in <see cref="StommeServiceCollectionExtensions"/>), and both are challenged ONLY
    /// explicitly by their login pages — exactly as OIDC is challenged by <c>Pages/External.cshtml</c>. Making
    /// Negotiate a default/global challenge would force Windows auth on every request and break the installed-site render
    /// guard; keeping it explicit is the whole safety story.
    /// </summary>
    public static class StommeExternalSchemesExtensions
    {
        /// <summary>The ASP.NET scheme name SAML2 registers under (the Sustainsys default).</summary>
        public static readonly string Saml2Scheme = Saml2Defaults.Scheme; // "Saml2"

        /// <summary>The ASP.NET scheme name Windows/Negotiate registers under (the Microsoft default).</summary>
        public static readonly string NegotiateScheme = NegotiateDefaults.AuthenticationScheme; // "Negotiate"

        /// <summary>
        /// A short-lived cookie SAML signs the RAW assertion principal into. The async <c>/signin-saml2</c>
        /// callback then reads it, runs the provisioning bridge, and swaps in the real Identity cookie. Using a
        /// dedicated external cookie (rather than Sustainsys's synchronous <c>AcsCommandResultCreated</c>
        /// notification) keeps the bridge fully async — the notification is an <c>Action</c>, so provisioning
        /// inside it would be sync-over-async, which this codebase bans.
        /// </summary>
        public const string Saml2ExternalScheme = "saml2.external";

        public static AuthenticationBuilder AddStommeExternalSchemes(
            this AuthenticationBuilder builder, IConfiguration configuration)
        {
            AddSaml2IfEnabled(builder, configuration);
            AddNegotiateIfEnabled(builder, configuration);
            return builder;
        }

        // ---- SAML2 (Sustainsys.Saml2.AspNetCore2) --------------------------------------------------------
        private static void AddSaml2IfEnabled(AuthenticationBuilder builder, IConfiguration configuration)
        {
            var saml = configuration.GetSection("Saml2");
            // Gate: Enabled AND the IdP is actually configured. An enabled-but-unconfigured section registers
            // nothing (a half-configured provider is a misconfiguration, not a hard startup failure).
            if (!saml.GetValue("Enabled", false)
                || string.IsNullOrWhiteSpace(saml["IdpEntityId"])
                || string.IsNullOrWhiteSpace(saml["IdpMetadataLocation"]))
            {
                return;
            }

            // The temp external cookie the SAML handler signs into before the async provisioning callback.
            builder.AddCookie(Saml2ExternalScheme, options =>
            {
                options.Cookie.Name = ".Stomme.Saml2.External";
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                options.SlidingExpiration = false;
                // The IdP POSTs the assertion cross-site to the ACS endpoint; the external cookie must survive
                // that top-level cross-site navigation to be readable at the callback.
                options.Cookie.SameSite = SameSiteMode.Unspecified;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            builder.AddSaml2(Saml2Scheme, options =>
            {
                options.SPOptions.EntityId = new EntityId(saml["SpEntityId"] ?? "https://stomme/saml");
                if (!string.IsNullOrWhiteSpace(saml["PublicOrigin"]))
                {
                    options.SPOptions.PublicOrigin = new Uri(saml["PublicOrigin"]!);
                }
                // Sign the raw assertion principal into the external cookie; the /signin-saml2 callback bridges
                // it to the Identity cookie. NEVER the Identity cookie directly (the raw SAML principal is not a
                // Stomme user identity until link-or-create runs).
                options.SignInScheme = Saml2ExternalScheme;
                options.IdentityProviders.Add(new IdentityProvider(new EntityId(saml["IdpEntityId"]!), options.SPOptions)
                {
                    MetadataLocation = saml["IdpMetadataLocation"]!,
                    LoadMetadata = true,
                    AllowUnsolicitedAuthnResponse = saml.GetValue("AllowUnsolicited", true),
                });
            });
        }

        // ---- Windows / Negotiate (Microsoft.AspNetCore.Authentication.Negotiate) --------------------------
        private static void AddNegotiateIfEnabled(AuthenticationBuilder builder, IConfiguration configuration)
        {
            if (!configuration.GetValue("WindowsAuth:Enabled", false))
            {
                return;
            }

            // The Negotiate handler is a Windows-runtime contract: on a non-Windows host without a configured
            // Kerberos stack it 500s on every request. Register it only on Windows unless an operator explicitly
            // opts in (WindowsAuth:AllowNonWindows for a Kerberos-configured Linux host). A warning would naturally be an
            // IHostedService, but that is not possible here — the assembly convention scan auto-registers EVERY
            // IHostedService in the host assembly unconditionally (StommeServiceCollectionExtensions:524), so
            // a warning service would run on a stock install too. A Debug trace is the gated-safe substitute.
            if (OperatingSystem.IsWindows() || configuration.GetValue("WindowsAuth:AllowNonWindows", false))
            {
                builder.AddNegotiate(NegotiateScheme, _ => { });
            }
            else
            {
                Debug.WriteLine("Stomme: WindowsAuth:Enabled=true but the host is not Windows and " +
                    "WindowsAuth:AllowNonWindows is not set — Negotiate NOT registered.");
            }
        }
    }
}
