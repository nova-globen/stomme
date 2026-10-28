using System;
using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Stomme.Repository;
using Stomme.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.OAuth;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Net;
using System.Text.Json.Nodes;
using System.Globalization;

namespace Stomme.Extensions
{
    public static class StommeSiteAuthenticationBuilderExtensions
    {
        public static StommeSiteOptionsBuilder WithSiteAuthentication(this StommeSiteOptionsBuilder builder)
        {
            // site cookie authentication options
            builder.AddSiteNamedOptions<CookieAuthenticationOptions>(Constants.AuthenticationScheme, (options, alias, sitesettings) =>
            {
                options.Cookie.Name = sitesettings.GetValue("LoginOptions:CookieName", ".AspNetCore.Identity.Application");
                if (!string.IsNullOrEmpty(sitesettings.GetValue("LoginOptions:CookieDomain", "")))
                {
                    options.Cookie.Domain = sitesettings.GetValue("LoginOptions:CookieDomain", "");
                }
                string cookieExpStr = sitesettings.GetValue("LoginOptions:CookieExpiration", "");
                if (!string.IsNullOrEmpty(cookieExpStr) && TimeSpan.TryParse(cookieExpStr, out TimeSpan cookieExpTS))
                {
                    options.Cookie.Expiration = cookieExpTS;
                    options.ExpireTimeSpan = cookieExpTS;
                }
            });

            // site OpenId Connect options
            builder.AddSiteOptions<OpenIdConnectOptions>((options, alias, sitesettings) =>
            {
                if (sitesettings.GetValue("ExternalLogin:ProviderType", "") == AuthenticationProviderTypes.OpenIDConnect)
                {
                    // default options
                    options.SignInScheme = Constants.AuthenticationScheme; // identity cookie
                    // RequireHttpsMetadata defaults true; a loopback/dev authority on http overrides it (D64).
                    options.RequireHttpsMetadata = bool.Parse(sitesettings.GetValue("ExternalLogin:RequireHttpsMetadata", "true"));
                    options.GetClaimsFromUserInfoEndpoint = true;
                    // MapInboundClaims defaults true (ASP.NET maps sub -> nameidentifier etc.). Set false to keep
                    // raw OIDC claim types (sub/name/email) so ExternalLogin:IdentifierClaimType can be "sub" (D64).
                    options.MapInboundClaims = bool.Parse(sitesettings.GetValue("ExternalLogin:MapInboundClaims", "true"));
                    options.CallbackPath = string.IsNullOrEmpty(alias.Path) ? "/signin-" + AuthenticationProviderTypes.OpenIDConnect : "/" + alias.Path + "/signin-" + AuthenticationProviderTypes.OpenIDConnect;
                    // FormPost is the secure default; a headless/loopback drive uses "query" so the code can be
                    // followed without a browser auto-submitting the form (D64).
                    options.ResponseMode = sitesettings.GetValue("ExternalLogin:ResponseMode", OpenIdConnectResponseMode.FormPost);

                    // cookie config is required to avoid Correlation Failed errors
                    options.NonceCookie.SameSite = SameSiteMode.Unspecified;
                    options.CorrelationCookie.SameSite = SameSiteMode.Unspecified;

                    // site options - an address the handler would refuse is never handed to it (BL-247): the options are
                    // built for every request, so a plain-http authority while https metadata is required threw on every
                    // request and the whole site answered 500, a restart included. Without it only signing in through this
                    // provider fails; the admin form refuses such an address when it is saved.
                    options.Authority = UsableMetadataAddress(sitesettings.GetValue("ExternalLogin:Authority", ""), options.RequireHttpsMetadata);
                    options.MetadataAddress = UsableMetadataAddress(sitesettings.GetValue("ExternalLogin:MetadataUrl", ""), options.RequireHttpsMetadata);
                    options.ClientId = sitesettings.GetValue("ExternalLogin:ClientId", "");
                    options.ClientSecret = sitesettings.GetValue("ExternalLogin:ClientSecret", "");
                    options.ResponseType = sitesettings.GetValue("ExternalLogin:AuthResponseType", "code"); // default is authorization code flow
                    options.ProtocolValidator.RequireNonce = bool.Parse(sitesettings.GetValue("ExternalLogin:RequireNonce", "true"));
                    options.UsePkce = bool.Parse(sitesettings.GetValue("ExternalLogin:PKCE", "false"));
                    options.SaveTokens = bool.Parse(sitesettings.GetValue("ExternalLogin:SaveTokens", "false"));
                    if (!string.IsNullOrEmpty(sitesettings.GetValue("ExternalLogin:RoleClaimType", "")))
                    {
                        options.TokenValidationParameters.RoleClaimType = sitesettings.GetValue("ExternalLogin:RoleClaimType", "");
                    }
                    options.Scope.Clear();
                    foreach (var scope in sitesettings.GetValue("ExternalLogin:Scopes", "openid,profile,email").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        options.Scope.Add(scope);
                    }

                    // openid connect events
                    options.Events.OnTokenValidated = OnTokenValidated;
                    options.Events.OnAccessDenied = OnAccessDenied;
                    options.Events.OnRemoteFailure = OnRemoteFailure;
                    if (sitesettings.GetValue("ExternalLogin:Parameters", "") != "")
                    {
                        options.Events.OnRedirectToIdentityProvider = context =>
                        {
                            foreach (var parameter in sitesettings.GetValue("ExternalLogin:Parameters", "").Split(","))
                            {
                                context.ProtocolMessage.SetParameter(parameter.Split("=")[0], parameter.Split("=")[1]);
                            }
                            return Task.FromResult(0);
                        };
                    }
                }
            });

            // site OAuth 2.0 options
            builder.AddSiteOptions<OAuthOptions>((options, alias, sitesettings) =>
            {
                if (sitesettings.GetValue("ExternalLogin:ProviderType", "") == AuthenticationProviderTypes.OAuth2)
                {
                    // default options
                    options.SignInScheme = Constants.AuthenticationScheme; // identity cookie
                    options.CallbackPath = string.IsNullOrEmpty(alias.Path) ? "/signin-" + AuthenticationProviderTypes.OAuth2 : "/" + alias.Path + "/signin-" + AuthenticationProviderTypes.OAuth2;

                    // site options
                    options.AuthorizationEndpoint = sitesettings.GetValue("ExternalLogin:AuthorizationUrl", "");
                    options.TokenEndpoint = sitesettings.GetValue("ExternalLogin:TokenUrl", "");
                    options.UserInformationEndpoint = sitesettings.GetValue("ExternalLogin:UserInfoUrl", "");
                    options.ClientId = sitesettings.GetValue("ExternalLogin:ClientId", "");
                    options.ClientSecret = sitesettings.GetValue("ExternalLogin:ClientSecret", "");
                    options.UsePkce = bool.Parse(sitesettings.GetValue("ExternalLogin:PKCE", "false"));
                    options.SaveTokens = bool.Parse(sitesettings.GetValue("ExternalLogin:SaveTokens", "false"));
                    options.Scope.Clear();
                    foreach (var scope in sitesettings.GetValue("ExternalLogin:Scopes", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        options.Scope.Add(scope);
                    }

                    // cookie config is required to avoid Correlation Failed errors
                    options.CorrelationCookie.SameSite = SameSiteMode.Unspecified;

                    // oauth2 events
                    options.Events.OnCreatingTicket = OnCreatingTicket;
                    options.Events.OnTicketReceived = OnTicketReceived;
                    options.Events.OnAccessDenied = OnAccessDenied;
                    options.Events.OnRemoteFailure = OnRemoteFailure;
                    if (sitesettings.GetValue("ExternalLogin:Parameters", "") != "")
                    {
                        options.Events.OnRedirectToAuthorizationEndpoint = context =>
                        {
                            var url = context.RedirectUri;
                            foreach (var parameter in sitesettings.GetValue("ExternalLogin:Parameters", "").Split(","))
                            {
                                url += (!url.Contains("?")) ? "?" + parameter : "&" + parameter;
                            }
                            context.Response.Redirect(url);
                            return Task.FromResult(0);
                        };
                    }
                }
            });

            return builder;
        }

        private static async Task OnCreatingTicket(OAuthCreatingTicketContext context)
        {
            // OAuth 2.0
            var claims = "";
            var id = "";
            var name = "";
            var email = "";

            if (context.Options.UserInformationEndpoint != "")
            {
                try
                {
                    // call user information endpoint using access token
                    var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.UserAgent.Add(new ProductInfoHeaderValue(Constants.PackageId, Constants.Version));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                    var response = await context.Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    claims = await response.Content.ReadAsStringAsync();

                    // get claim types
                    var idClaimType = context.HttpContext.GetSiteSettings().GetValue("ExternalLogin:IdentifierClaimType", "");
                    var nameClaimType = context.HttpContext.GetSiteSettings().GetValue("ExternalLogin:NameClaimType", "");
                    var emailClaimType = context.HttpContext.GetSiteSettings().GetValue("ExternalLogin:EmailClaimType", "");

                    // some user endpoints can return multiple objects (ie. GitHub) so convert single object to array (if necessary)
                    var jsonclaims = claims;
                    if (!jsonclaims.StartsWith("[") && !jsonclaims.EndsWith("]"))
                    {
                        jsonclaims = "[" + jsonclaims + "]"; 
                    }

                    // parse claim values
                    JsonNode items = JsonNode.Parse(jsonclaims)!;
                    foreach (var item in items.AsArray())
                    {
                        name = "";
                        email = "";

                        // id claim is required
                        if (!string.IsNullOrEmpty(idClaimType) && item[idClaimType] != null)
                        {
                            id = item[idClaimType].ToString();

                            // name claim is optional
                            if (!string.IsNullOrEmpty(nameClaimType) && item[nameClaimType] != null)
                            {
                                name = item[nameClaimType].ToString();
                            }

                            // email claim is optional
                            if (!string.IsNullOrEmpty(emailClaimType) && item[emailClaimType] != null)
                            {
                                if (EmailValid(item[emailClaimType].ToString(), context.HttpContext.GetSiteSettings().GetValue("ExternalLogin:DomainFilter", "")))
                                {
                                    email = item[emailClaimType].ToString().ToLower();
                                }
                                else
                                {
                                    id = ""; // if email is not valid we will assume id is not valid
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(id))
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    var _logger = context.HttpContext.RequestServices.GetRequiredService<ILogManager>();
                    _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, ex, "An Error Occurred Accessing The User Info Endpoint - {Error}", ex.Message);
                }
            }

            // validate user
            var identity = await ValidateUser(id, name, email, claims, context.HttpContext, context.Principal);
            if (identity.Label == ExternalLoginStatus.Success)
            {
                context.Principal = new ClaimsPrincipal(identity);
            }

            // pass properties to OnTicketReceived
            context.Properties.SetParameter("status", identity.Label);
            context.Properties.SetParameter("redirecturl", context.Properties.RedirectUri);

            // set cookie expiration
            string cookieExpStr = context.HttpContext.GetSiteSettings().GetValue("LoginOptions:CookieExpiration", "");
            if (!string.IsNullOrEmpty(cookieExpStr) && TimeSpan.TryParse(cookieExpStr, out TimeSpan cookieExpTS))
            {
                context.Properties.ExpiresUtc = DateTime.Now.Add(cookieExpTS);
                context.Properties.IsPersistent = true;
            }
        }

        private static Task OnTicketReceived(TicketReceivedContext context)
        {
            // OAuth 2.0
            var status = context.Properties.GetParameter<string>("status");
            if (status != ExternalLoginStatus.Success)
            {
                // redirect to login page and pass status
                context.Response.Redirect(Utilities.TenantUrl(context.HttpContext.GetAlias(), $"/login?status={status}&returnurl={context.Properties.GetParameter<string>("redirecturl")}"), true);
                context.HandleResponse();
            }
            return Task.CompletedTask;
        }

        private static async Task OnTokenValidated(TokenValidatedContext context)
        {
            // OpenID Connect
            var claims = "";
            var id = "";
            var name = "";
            var email = "";

            // serialize claims
            foreach (var claim in context.Principal.Claims)
            {
                claims += "\"" + claim.Type + "\":\"" + claim.Value + "\",";
            }
            claims = "{" + claims.Substring(0, claims.Length - 1) + "}";

            // get claim types
            var idClaimType = context.HttpContext.GetSiteSettings().GetValue("ExternalLogin:IdentifierClaimType", "");
            var nameClaimType = context.HttpContext.GetSiteSettings().GetValue("ExternalLogin:NameClaimType", "");
            var emailClaimType = context.HttpContext.GetSiteSettings().GetValue("ExternalLogin:EmailClaimType", "");

            // parse claim values - id claim is required
            id = context.Principal.FindFirstValue(idClaimType);

            // name claim is optional
            if (!string.IsNullOrEmpty(nameClaimType) && context.Principal.FindFirstValue(nameClaimType) != null)
            {
                name = context.Principal.FindFirstValue(nameClaimType);
            }

            // email claim is optional
            if (!string.IsNullOrEmpty(emailClaimType) && context.Principal.FindFirstValue(emailClaimType) != null)
            {
                if (EmailValid(context.Principal.FindFirstValue(emailClaimType), context.HttpContext.GetSiteSettings().GetValue("ExternalLogin:DomainFilter", "")))
                {
                    email = context.Principal.FindFirstValue(emailClaimType);
                }
                else
                {
                    id = ""; // if email is not valid we will assume id is not valid
                }
            }

            // validate user
            var identity = await ValidateUser(id, name, email, claims, context.HttpContext, context.Principal);
            if (identity.Label == ExternalLoginStatus.Success)
            {
                context.Principal = new ClaimsPrincipal(identity);
            }
            else
            {
                // redirect to login page and pass status
                context.Response.Redirect(Utilities.TenantUrl(context.HttpContext.GetAlias(), $"/login?status={identity.Label}&returnurl={context.Properties.RedirectUri}"), true);
                context.HandleResponse();
            }
        }

        /// <summary>
        /// The authority or metadata address as configured, or empty when the OpenID Connect handler would refuse it - a
        /// plain-http address while https metadata is required - so the site keeps serving (BL-247).
        /// </summary>
        internal static string UsableMetadataAddress(string address, bool requireHttps) =>
            string.IsNullOrEmpty(address) || !requireHttps || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? address : string.Empty;

        private static Task OnAccessDenied(AccessDeniedContext context)
        {
            var _logger = context.HttpContext.RequestServices.GetRequiredService<ILogManager>();
            _logger.Log(LogLevel.Information, "ExternalLogin", Enums.LogFunction.Security, "External Login Access Denied - User May Have Cancelled Their External Login Attempt");
            // redirect to login page
            context.Response.Redirect(Utilities.TenantUrl(context.HttpContext.GetAlias(), $"/login?status={ExternalLoginStatus.AccessDenied}&returnurl={context.Properties.RedirectUri}"), true);
            context.HandleResponse();
            return Task.CompletedTask;
        }

        private static Task OnRemoteFailure(RemoteFailureContext context)
        {
            var _logger = context.HttpContext.RequestServices.GetRequiredService<ILogManager>();
            _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "External Login Remote Failure - {Error}", context.Failure.Message);
            // redirect to login page
            context.Response.Redirect(Utilities.TenantUrl(context.HttpContext.GetAlias(), $"/login?status={ExternalLoginStatus.RemoteFailure}"), true);
            context.HandleResponse();
            return Task.CompletedTask;
        }

        /// <summary>
        /// D65 — the SAML2 / Windows-Negotiate provisioning bridge. A successful SAML assertion or Negotiate
        /// handshake yields a <see cref="ClaimsPrincipal"/>; this routes it through the SAME link-or-create path
        /// the OIDC/OAuth consumers use (<see cref="ValidateUser"/>), so an external login creates-or-links an
        /// <c>IdentityUser</c> + writes a <c>UserLogin</c> row and produces the Identity-cookie
        /// <see cref="ClaimsIdentity"/>. The scheme is installation-wide, but linkage stays PER-SITE: the
        /// <paramref name="providerType"/> becomes the UserLogin provider prefix and <c>ValidateUser</c> appends
        /// the resolved <c>:{siteId}</c>. Fully async — the callers (the async <c>/signin-saml2</c> callback and
        /// the Windows-login page) await this, so no sync-over-async is introduced (D33).
        /// </summary>
        internal static Task<ClaimsIdentity> ProvisionExternalPrincipalAsync(
            HttpContext httpContext, ClaimsPrincipal principal, string providerType, string providerName,
            string idClaimType, string nameClaimType, string emailClaimType)
        {
            // Extract the required identifier + optional name/email from the incoming principal, mirroring the
            // OIDC OnTokenValidated extraction but driven by installation config claim-types (SAML/Windows use
            // ClaimTypes.* URIs by default, not "sub"/"name"/"email").
            var id = principal.FindFirstValue(idClaimType) ?? "";
            var name = !string.IsNullOrEmpty(nameClaimType) ? principal.FindFirstValue(nameClaimType) ?? "" : "";
            var email = !string.IsNullOrEmpty(emailClaimType) ? principal.FindFirstValue(emailClaimType) ?? "" : "";

            // serialize claims (same shape as OnTokenValidated, used by the ReviewClaims diagnostic)
            var claims = "";
            foreach (var claim in principal.Claims)
            {
                claims += "\"" + claim.Type + "\":\"" + claim.Value + "\",";
            }
            claims = claims.Length > 0 ? "{" + claims.Substring(0, claims.Length - 1) + "}" : "{}";

            return ValidateUser(id, name, email, claims, httpContext, principal, providerType, providerName);
        }

        private static async Task<ClaimsIdentity> ValidateUser(string id, string name, string email, string claims, HttpContext httpContext, ClaimsPrincipal claimsPrincipal, string providerTypeOverride = null, string providerNameOverride = null)
        {
            var _logger = httpContext.RequestServices.GetRequiredService<ILogManager>();
            ClaimsIdentity identity = new ClaimsIdentity(Constants.AuthenticationScheme);
            // use identity.Label as a temporary location to store validation status information

            // review claims feature (for testing - external login is disabled)
            if (bool.Parse(httpContext.GetSiteSettings().GetValue("ExternalLogin:ReviewClaims", "false")))
            {
                _logger.Log(LogLevel.Information, "ExternalLogin", Enums.LogFunction.Security, "Provider Returned The Following Claims: {Claims}", claims);
                identity.Label = ExternalLoginStatus.ReviewClaims;
                return identity;
            }

            // D65: SAML/Negotiate pass the provider type/name explicitly (they are installation-wide schemes,
            // not per-site ExternalLogin config). OIDC/OAuth pass null and keep reading the site settings.
            var providerType = providerTypeOverride ?? httpContext.GetSiteSettings().GetValue("ExternalLogin:ProviderType", "");
            var providerName = providerNameOverride ?? httpContext.GetSiteSettings().GetValue("ExternalLogin:ProviderName", "");
            var alias = httpContext.GetAlias();
            var _users = httpContext.RequestServices.GetRequiredService<IUserRepository>();
            User user = null;
            // D65: on the persistence seam, AddUserAsync TRACKS the new UserAggregate in the store's DbContext
            // (D19). A later UpdateUserAsync in the same request attaches a second instance with the same key,
            // which EF rejects ("cannot be tracked because another instance ... is already being tracked") — the
            // create-then-update path, never exercised before D65 (D64 only drove the LINK path). So a
            // just-created user persists LastLoginOn/LastIPAddress at INSERT time and skips the redundant update.
            bool userCreated = false;

            if (!string.IsNullOrEmpty(id))
            {
                // verify if external user is already registered for this site
                var _identityUserManager = httpContext.RequestServices.GetRequiredService<UserManager<IdentityUser>>();
                var identityuser = await _identityUserManager.FindByLoginAsync(providerType + ":" + alias.SiteId.ToString(), id);
                if (identityuser != null)
                {
                    user = await _users.GetUserAsync(identityuser.UserName);
                    user.SiteId = alias.SiteId;
                    if ((!string.IsNullOrEmpty(email) && user.Email != email) || (!string.IsNullOrEmpty(name) && user.DisplayName != name))
                    {
                        // synchronize email and displayname
                        user.Email = !string.IsNullOrEmpty(email) ? email : user.Email;
                        user.DisplayName = !string.IsNullOrEmpty(name) ? name : user.DisplayName;
                        await _users.UpdateUserAsync(user);
                        if (identityuser.Email != user.Email)
                        {
                            identityuser.Email = user.Email;
                            await _identityUserManager.UpdateAsync(identityuser); // security stamp not updated
                        }
                    }
                }
                else
                {
                    bool duplicates = false;
                    if (!string.IsNullOrEmpty(email))
                    {
                        try
                        {
                            identityuser = await _identityUserManager.FindByEmailAsync(email);
                        }
                        catch // FindByEmailAsync will throw an error if the email matches multiple user accounts
                        {                                
                            duplicates = true;
                        }
                    }
                    if (identityuser == null)
                    {
                        if (duplicates)
                        {
                            identity.Label = ExternalLoginStatus.DuplicateEmail;
                            _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "Multiple Users Exist With Email Address {Email}. Login Denied.", email);
                        }
                        else
                        {
                            if (bool.Parse(httpContext.GetSiteSettings().GetValue("ExternalLogin:CreateUsers", "true")))
                            {
                                // user identifiers
                                var username = "";
                                var emailaddress = "";
                                var displayname = "";
                                bool emailconfirmed = false;

                                if (!string.IsNullOrEmpty(email)) // email claim provided
                                {
                                    username = email;
                                    emailaddress = email;
                                    displayname = (!string.IsNullOrEmpty(name)) ? name : email;
                                    emailconfirmed = true;
                                }
                                else if (!string.IsNullOrEmpty(name)) // name claim provided
                                {
                                    username = name.ToLower().Replace(" ", "") + DateTime.UtcNow.ToString("mmss");
                                    emailaddress = username + "@unknown.com";
                                    displayname = name;
                                }
                                else // neither email nor name provided
                                {
                                    username = Guid.NewGuid().ToString("N"); 
                                    emailaddress = username + "@unknown.com";
                                    displayname = username;
                                }

                                identityuser = new IdentityUser();
                                identityuser.UserName = username;
                                identityuser.Email = emailaddress;
                                identityuser.EmailConfirmed = emailconfirmed;

                                // generate password based on random date and punctuation ie. Jan-23-1981+14:43:12!
                                Random rnd = new Random();
                                var date = DateTime.UtcNow.AddDays(-rnd.Next(50 * 365)).AddHours(rnd.Next(0, 24)).AddMinutes(rnd.Next(0, 60)).AddSeconds(rnd.Next(0, 60));
                                var password = date.ToString("MMM-dd-yyyy+HH:mm:ss", CultureInfo.InvariantCulture) + (char)rnd.Next(33, 47);

                                var result = await _identityUserManager.CreateAsync(identityuser, password);
                                if (result.Succeeded)
                                {
                                    user = new User
                                    {
                                        SiteId = alias.SiteId,
                                        Username = username,
                                        DisplayName = displayname,
                                        Email = emailaddress,
                                        // persist the first-login stamp at INSERT (see userCreated note above)
                                        LastLoginOn = DateTime.UtcNow,
                                        LastIPAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? ""
                                    };
                                    user = await _users.AddUserAsync(user);

                                    if (user != null)
                                    {
                                        userCreated = true;

                                        if (!string.IsNullOrEmpty(email))
                                        {
                                            var _notifications = httpContext.RequestServices.GetRequiredService<INotificationRepository>();
                                            string url = httpContext.Request.Scheme + "://" + alias.Name;
                                            string body = "You Recently Used An External Account To Sign In To Our Site.\n\n" + url + "\n\nThank You!";
                                            var notification = new Notification(user.SiteId, user, "User Account Notification", body);
                                            await _notifications.AddNotificationAsync(notification);
                                        }

                                        // add user login
                                        await _identityUserManager.AddLoginAsync(identityuser, new UserLoginInfo(providerType + ":" + user.SiteId.ToString(), id, providerName));

                                        _logger.Log(user.SiteId, LogLevel.Information, "ExternalLogin", Enums.LogFunction.Create, "User Added {User}", user);
                                    }
                                    else
                                    {
                                        identity.Label = ExternalLoginStatus.UserNotCreated;
                                        _logger.Log(alias.SiteId, LogLevel.Error, "ExternalLogin", Enums.LogFunction.Create, "Unable To Add User {Email}", email);
                                    }
                                }
                                else
                                {
                                    identity.Label = ExternalLoginStatus.UserNotCreated;
                                    _logger.Log(alias.SiteId, LogLevel.Error, "ExternalLogin", Enums.LogFunction.Create, "Unable To Add Identity User {Email} {Error}", email, result.Errors.ToString());
                                }
                            }
                            else
                            {
                                identity.Label = ExternalLoginStatus.UserDoesNotExist;
                                _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "Creation Of New Users Is Disabled For This Site. User With Email Address {Email} Will First Need To Be Registered On The Site.", email);
                            }
                        }
                    }
                    else
                    {
                        var logins = await _identityUserManager.GetLoginsAsync(identityuser);
                        // check if any logins exist for this user and provider type for any site
                        var login = logins.FirstOrDefault(item => item.LoginProvider.StartsWith(providerType));
                        if (login != null || !bool.Parse(httpContext.GetSiteSettings().GetValue("ExternalLogin:VerifyUsers", "true")))
                        {
                            // external login using existing user account - link automatically
                            user = await _users.GetUserAsync(identityuser.UserName);
                            user.SiteId = alias.SiteId;

                            var _notifications = httpContext.RequestServices.GetRequiredService<INotificationRepository>();
                            string url = httpContext.Request.Scheme + "://" + alias.Name;
                            string body = "You Recently Used An External Account To Sign In To Our Site.\n\n" + url + "\n\nThank You!";
                            var notification = new Notification(user.SiteId, user, "User Account Notification", body);
                            await _notifications.AddNotificationAsync(notification);

                            // add user login
                            await _identityUserManager.AddLoginAsync(identityuser, new UserLoginInfo(providerType + ":" + user.SiteId.ToString(), id, providerName));

                            _logger.Log(user.SiteId, LogLevel.Information, "ExternalLogin", Enums.LogFunction.Create, "External Login Linkage Created For User {Username} And Provider {Provider}", user.Username, providerName);
                        }
                        else
                        {
                            if (bool.Parse(httpContext.GetSiteSettings().GetValue("ExternalLogin:VerifyUsers", "true")))
                            {
                                // external login using existing user account - verification required
                                var _notifications = httpContext.RequestServices.GetRequiredService<INotificationRepository>();
                                string token = await _identityUserManager.GenerateEmailConfirmationTokenAsync(identityuser);
                                string url = httpContext.Request.Scheme + "://" + alias.Name;
                                url += $"/login?name={identityuser.UserName}&token={WebUtility.UrlEncode(token)}&key={WebUtility.UrlEncode(id)}";
                                string body = $"You Recently Signed In To Our Site With {providerName} Using The Email Address {email}. ";
                                body += "In Order To Complete The Linkage Of Your User Account Please Click The Link Displayed Below:\n\n" + url + "\n\nThank You!";
                                var notification = new Notification(alias.SiteId, email, email, "External Login Linkage", body);
                                await _notifications.AddNotificationAsync(notification);

                                identity.Label = ExternalLoginStatus.VerificationRequired;
                                _logger.Log(alias.SiteId, LogLevel.Information, "ExternalLogin", Enums.LogFunction.Create, "External Login Linkage Verification For Provider {Provider} Sent To {Email}", providerName, email);
                            }
                            else
                            {
                                // provider keys do not match
                                identity.Label = ExternalLoginStatus.ProviderKeyMismatch;
                                _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "Provider Key Does Not Match For User {Username}. Login Denied.", identityuser.UserName);
                            }
                        }
                    }
                }

                // manage user
                if (user != null)
                {
                    // manage roles
                    var _roles = httpContext.RequestServices.GetRequiredService<IRoleRepository>();
                    var _userRoles = httpContext.RequestServices.GetRequiredService<IUserRoleRepository>();
                    var userRoles = (await _userRoles.GetUserRolesAsync(user.UserId, user.SiteId)).ToList();

                    // if user is signing in to a new site
                    if (userRoles.Count == 0)
                    {
                        // add auto assigned roles to user for site
                        var roles = (await _roles.GetRolesAsync(user.SiteId)).Where(item => item.IsAutoAssigned).ToList();
                        foreach (var role in roles)
                        {
                            var userrole = new UserRole();
                            userrole.UserId = user.UserId;
                            userrole.RoleId = role.RoleId;
                            userrole.EffectiveDate = null;
                            userrole.ExpiryDate = null;
                            userrole.IgnoreSecurityStamp = true;
                            await _userRoles.AddUserRoleAsync(userrole);
                        }
                        userRoles = (await _userRoles.GetUserRolesAsync(user.UserId, user.SiteId)).ToList();
                    }

                    // process any role claims
                    if (!string.IsNullOrEmpty(httpContext.GetSiteSettings().GetValue("ExternalLogin:RoleClaimType", "")))
                    {
                        // external roles
                        if (claimsPrincipal.Claims.Any(item => item.Type == httpContext.GetSiteSettings().GetValue("ExternalLogin:RoleClaimType", "")))
                        {
                            var allowhostrole = bool.Parse(httpContext.GetSiteSettings().GetValue("ExternalLogin:AllowHostRole", "false"));
                            var roles = (await _roles.GetRolesAsync(user.SiteId, allowhostrole)).ToList();

                            var mappings = httpContext.GetSiteSettings().GetValue("ExternalLogin:RoleClaimMappings", "").Split(',');
                            foreach (var claim in claimsPrincipal.Claims.Where(item => item.Type == httpContext.GetSiteSettings().GetValue("ExternalLogin:RoleClaimType", "")))
                            {
                                var rolename = claim.Value;
                                if (mappings.Any(item => item.StartsWith(rolename + ":")))
                                {
                                    rolename = mappings.First(item => item.StartsWith(rolename + ":")).Split(':')[1];
                                }
                                var role = roles.FirstOrDefault(item => item.Name == rolename);
                                if (role != null)
                                {
                                    if (!userRoles.Any(item => item.RoleId == role.RoleId && item.UserId == user.UserId))
                                    {
                                        var userRole = new UserRole();
                                        userRole.RoleId = role.RoleId;
                                        userRole.UserId = user.UserId;
                                        await _userRoles.AddUserRoleAsync(userRole);
                                    }
                                }
                            }
                            if (bool.Parse(httpContext.GetSiteSettings().GetValue("ExternalLogin:SynchronizeRoles", "false")))
                            {
                                userRoles = (await _userRoles.GetUserRolesAsync(user.UserId, user.SiteId)).ToList();
                                foreach (var userRole in userRoles)
                                {
                                    var role = roles.FirstOrDefault(item => item.RoleId == userRole.RoleId);
                                    if (role != null)
                                    {
                                        var rolename = role.Name;
                                        if (mappings.Any(item => item.EndsWith(":" + rolename)))
                                        {
                                            rolename = mappings.First(item => item.EndsWith(":" + rolename)).Split(':')[0];
                                        }
                                        if (!claimsPrincipal.Claims.Any(item => item.Type == httpContext.GetSiteSettings().GetValue("ExternalLogin:RoleClaimType", "") && item.Value == rolename))
                                        {
                                            await _userRoles.DeleteUserRoleAsync(userRole.UserRoleId);
                                        }
                                    }
                                }
                            }
                            userRoles = (await _userRoles.GetUserRolesAsync(user.UserId, user.SiteId)).ToList();
                        }
                        else
                        {
                            _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "The Role Claim {ClaimType} Does Not Exist. Please Use The Review Claims Feature To View The Claims Returned By Your Provider.", httpContext.GetSiteSettings().GetValue("ExternalLogin:RoleClaimType", ""));
                        }
                    }

                    var host = userRoles.FirstOrDefault(item => item.Role.Name == RoleNames.Host);
                    var registered = userRoles.FirstOrDefault(item => item.Role.Name == RoleNames.Registered);
                    // BL-224: a locked-out account signs in by no route. The password sign-in checks lockout; an external one
                    // now does too, as ASP.NET Identity's own external sign-in does - and a service account is locked out for good.
                    var lockedOut = await _identityUserManager.FindByNameAsync(user.Username) is { } lockoutCandidate
                        && await _identityUserManager.IsLockedOutAsync(lockoutCandidate);
                    if (!user.IsDeleted && !lockedOut && (host != null || registered != null && Utilities.IsEffectiveAndNotExpired(registered.EffectiveDate, registered.ExpiryDate)))
                    {
                        // update user
                        user.LastLoginOn = DateTime.UtcNow;
                        // D70: RemoteIpAddress is null when a reverse proxy strips the connection IP (an external
                        // login with no connection endpoint), so guard it — an unguarded .ToString() NREs and
                        // fails the whole login. Matches the empty-IP convention used elsewhere (line ~495,
                        // App.razor, LogManager).
                        user.LastIPAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "";
                        // A just-created user already persisted these at INSERT; re-updating here attaches a
                        // second UserAggregate with the same key to the store's tracked context and EF rejects it
                        // (D19 / D65). The in-memory user still carries the values for the claims identity below.
                        if (!userCreated)
                        {
                            await _users.UpdateUserAsync(user);
                        }

                        // create claims identity
                        identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                        user.SecurityStamp = identityuser.SecurityStamp;
                        identity = UserSecurity.CreateClaimsIdentity(alias, user, userRoles);
                        identity.Label = ExternalLoginStatus.Success;

                        // user profile claims
                        if (!string.IsNullOrEmpty(httpContext.GetSiteSettings().GetValue("ExternalLogin:ProfileClaimTypes", "")))
                        {
                            var _settings = httpContext.RequestServices.GetRequiredService<ISettingRepository>();
                            var _profiles = httpContext.RequestServices.GetRequiredService<IProfileRepository>();
                            var profiles = (await _profiles.GetProfilesAsync(alias.SiteId)).ToList();
                            foreach (var mapping in httpContext.GetSiteSettings().GetValue("ExternalLogin:ProfileClaimTypes", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (mapping.Contains(":"))
                                {
                                    var claim = claimsPrincipal.Claims.FirstOrDefault(item => item.Type == mapping.Split(":")[0]);
                                    if (claim != null)
                                    {
                                        var profile = profiles.FirstOrDefault(item => item.Name == mapping.Split(":")[1]);
                                        if (profile != null)
                                        {
                                            if (!string.IsNullOrEmpty(claim.Value))
                                            {
                                                var setting = await _settings.GetSettingAsync(EntityNames.User, user.UserId, profile.Name);
                                                if (setting != null)
                                                {
                                                    setting.SettingValue = claim.Value;
                                                    await _settings.UpdateSettingAsync(setting);
                                                }
                                                else
                                                {
                                                    setting = new Setting { EntityName = EntityNames.User, EntityId = user.UserId, SettingName = profile.Name, SettingValue = claim.Value, IsPrivate = profile.IsPrivate };
                                                    await _settings.AddSettingAsync(setting);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "The User Profile {ProfileName} Does Not Exist For The Site. Please Verify Your User Profile Definitions.", mapping.Split(":")[1]);
                                        }
                                    }
                                    else
                                    {
                                        _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "The User Profile Claim {ClaimType} Does Not Exist. Please Use The Review Claims Feature To View The Claims Returned By Your Provider.", mapping.Split(":")[0]);
                                    }
                                }
                                else
                                {
                                    _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "The User Profile Claim Mapping {Mapping} Is Not Specified Correctly. It Should Be In The Format 'ClaimType:ProfileName'.", mapping);
                                }
                            }
                        }

                        var _syncManager = httpContext.RequestServices.GetRequiredService<ISyncManager>();
                        await _syncManager.AddSyncEventAsync(alias, EntityNames.User, user.UserId, "Login");

                        _logger.Log(LogLevel.Information, "ExternalLogin", Enums.LogFunction.Security, "External Login Successful For {Username} From IP Address {IPAddress} Using Provider {Provider}", user.Username, httpContext.Connection.RemoteIpAddress?.ToString() ?? "", providerName);
                    }
                    else
                    {
                        identity.Label = ExternalLoginStatus.AccessDenied;
                        _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "External Login Denied For {Username}. User Account Is Deleted, Locked Out Or Not An Active Member Of Site {SiteId}.", user.Username, user.SiteId);
                    }
                }
            }
            else // claims invalid
            {
                identity.Label = ExternalLoginStatus.MissingClaims;
                _logger.Log(LogLevel.Error, "ExternalLogin", Enums.LogFunction.Security, "Provider Did Not Return All Of The Claims Types Specified Or Email Address Does Not Satisfy Domain Filter. The Actual Claims Returned Were {Claims}. Login Was Denied.", claims);
            }

            return identity;
        }

        private static bool EmailValid(string email, string domainfilter)
        {
            if (!string.IsNullOrEmpty(email))
            {
                if (email.Contains("@") && email.Contains("."))
                {
                    var domains = domainfilter.ToLower().Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var domain in domains)
                    {
                        if (domain.StartsWith("!"))
                        {
                            if (email.ToLower().Contains(domain.Substring(1))) return false;
                        }
                        else
                        {
                            if (!email.ToLower().Contains(domain)) return false;
                        }
                    }
                    return true;
                }
                return false;
            }            
            return (string.IsNullOrEmpty(domainfilter)); // email is optional unless domain filter is specified
        }
    }
}
