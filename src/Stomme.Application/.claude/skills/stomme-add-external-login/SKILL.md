---
name: stomme-add-external-login
description: Use when a Nova Globen Stomme site must let people sign in with an external account - OpenID Connect or OAuth 2.0 (Entra ID, Google, Okta, Keycloak, another Stomme), SAML 2.0, or Windows/Negotiate - including mapping the provider's claims and roles, or when an external sign-in fails, links the wrong account, or shows no button.
---

# Add an external login

You will have a site whose login page offers a second way in - the site's OpenID Connect or OAuth 2.0 provider, SAML
2.0, or Windows sign-in - that ends in an ordinary Stomme user with the site's roles. No app code is needed: the
per-site provider is site settings, SAML and Windows are configuration switches. Read
[authentication-options.md](../../../docs/security/authentication-options.md) first; it has every key and the
link-or-create rules.

## Steps

### A. OpenID Connect or OAuth 2.0, per site

1. **Register a client at the provider.** Redirect URI: `https://{site host}/signin-oidc` for OpenID Connect,
   `https://{site host}/signin-oauth2` for OAuth 2.0; for a virtual-folder alias, `https://{host}/{alias path}/signin-oidc`
   (the settings form shows it as **Redirect Url** once a provider type is chosen). Note the client id, the secret, and
   the authority (the issuer URL whose `/.well-known/openid-configuration` exists; it must be `https://`). Ask the
   provider to release the e-mail claim. Another Stomme installation can be the provider: register this site as a
   client there with the [stomme-oidc-provider](../stomme-oidc-provider/SKILL.md) skill (`authorization_code`,
   scopes `openid`, `profile`, `email`).
2. **Sign in as the Host** and open **Admin > User Management > Settings** (Host only), section **External Login
   Settings**.
3. Leave **Provider** at `<Custom>` (or pick a preset: Auth0, Facebook, GitHub, Microsoft Entra), choose the
   **Provider Type** (`OpenID Connect (OIDC)` or `OAuth 2.0 (OAuth2)`), give it a **Provider Name** (the button reads
   "Use {Provider Name}"), and fill in the **Authority** (or the **Metadata Url**) for OpenID Connect, or the
   authorization, token and user-info URLs for OAuth 2.0; the **Client ID** and **Client Secret**; the **Scopes**
   (`openid,profile,email` is pre-filled). Set **Use PKCE?** to Yes if the provider supports it.
   **The authority must be `https://`.** The form refuses an `http://` Authority or Metadata Url ("must start with
   https://"); a value saved another way is not used, and signing in through the provider fails until it is fixed.
4. **Find the claim types first.** Set **Review Claims?** to Yes, **Save**, sign out, and sign in through the
   provider's button once. You come back to the login page with "The Review Claims Option Was Enabled In External Login
   Settings. Please Visit The Event Log To View The Claims Returned By The Provider." and are NOT signed in. **Admin >
   Event Log** (Host only) has the entry "Provider Returned The Following Claims: {...}". Set **Identifier Claim**,
   **Name Claim** and **Email Claim** to types from that list and set **Review Claims?** back to No.
   The form pre-fills `sub`, `name` and `email`, and for OpenID Connect those are usually WRONG: the handler maps the
   token's claims, so the list shows `sub` as `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`
   and `email` as `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` (`name` stays `name`). Use those
   full types.
5. Decide the account rules: keep **Verify Users** on (an existing account with the same e-mail is linked only after
   its owner clicks the e-mailed link), keep **Create Users** on or off as the site needs, and set a **Domain Filter**
   to accept only your organisation's addresses.
6. Optional roles: set the role claim type and mappings (`providerRole:StommeRole`, comma-separated); **Synchronize
   Roles** removes mapped roles the provider no longer sends. Leave **Allow Host Role** off.
7. **Save** ("Settings Saved Successfully"). The change applies at once, no restart. The login page now shows the
   "Use {Provider Name}" button; it sends people to `/pages/external`.

### B. SAML 2.0, for the installation

1. Put the switch and the identity provider in configuration (environment variables shown; any configuration source
   works for these keys):

   ```
   Saml2__Enabled=true
   Saml2__IdpEntityId=https://idp.example.com/saml
   Saml2__IdpMetadataLocation=https://idp.example.com/saml/metadata
   Saml2__SpEntityId=https://app.example.com/saml
   Saml2__PublicOrigin=https://app.example.com
   ```

2. Put the claim types (and `ProviderName`) in `Server/appsettings.json`, environment variables or the installation's
   `appsettings.override.json` (any source since 10.73.2; on an older Stomme, not the override file):

   ```json
   "Saml2": {
     "ProviderName": "Company SSO",
     "IdentifierClaimType": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
     "NameClaimType": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
     "EmailClaimType": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"
   }
   ```

   The values shown are the defaults (`ClaimTypes.NameIdentifier`, `.Name`, `.Email`); change them to what your
   provider sends.
3. Register this service provider at the identity provider with the entity id you set. The SAML library is
   Sustainsys.Saml2 with its default module path; its metadata and assertion consumer endpoints are under `/Saml2`
   (library default - check the metadata the running app serves rather than trusting this line).
4. Restart the app: the switches are read at startup.

### C. Windows (Negotiate), for the installation

1. Set `WindowsAuth__Enabled=true` (and `WindowsAuth__AllowNonWindows=true` only on a Linux host with Kerberos
   configured). Claim types (`WindowsAuth:IdentifierClaimType`, `NameClaimType`, `EmailClaimType`) go in
   `appsettings.json` or environment variables, as for SAML. **Set the name claim type**: a Windows identity has no
   e-mail and its name is `DOMAIN\user`, which ASP.NET Identity refuses as a user name, so with the defaults every new
   user ends on the login page with "A User Account Could Not Be Created For Your External Login. ...". Use the SID:

   ```
   WindowsAuth__NameClaimType=http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid
   ```

   The new account's user name and display name are then derived from the SID; rename the display name in **Admin >
   User Management** if people see it.
2. Host it where the browser can negotiate: an intranet zone, a reverse proxy that passes the `Authorization` header
   through.
3. Restart. The button sends people to `/pages/windows-login`.

The account rules of steps A5 and A6 - **Verify Users**, **Create Users** and the role-claim settings - are site settings
and apply to SAML and Windows sign-in on that site too. The **Domain Filter** does not: it is applied only to the
site's OpenID Connect and OAuth 2.0 provider.

## Verify

Steps 1 to 4 and 6 (domain filter) were driven with a second local Stomme app as the OpenID Connect provider, in a
browser over plain HTTP on `*.localhost` host names; steps 5 and the lockout half of 6 were not driven. SAML was not
driven (no identity provider); Windows sign-in was driven on Windows with the machine's own account.

1. In a private browser window open the site's login page: the "Use {Provider Name}" button is there (and is not, for
   a switch that is off). `/pages/external` on a site without a provider type, and `/pages/saml-login` or
   `/pages/windows-login` with the scheme off, answer 403.
2. Sign in with an account the site has never seen: the provider's login page, then you land on the return URL signed
   in. **Admin > User Management** lists the new user - its user name is the e-mail address - with `Registered Users`
   (and your mapped roles).
3. Sign out and in again: the same Stomme user, no duplicate.
4. With an existing Stomme account whose e-mail matches a provider account, sign in through the provider: with
   **Verify Users** on, you come back to the login page NOT signed in (`status=VerificationRequired`) and a linkage
   e-mail is queued (it arrives only if the site's mail is configured).
5. On a second site of the same tenant, sign in through the provider: the account is linked for that site separately and
   holds that site's roles only.
6. A user outside the domain filter (OpenID Connect / OAuth 2.0) is refused on the login page with "The External Login
   Provider Did Not Provide All Of The Required Information" - also when the account was linked before. A user locked
   out in **Admin > User Management** is refused too. Never signed in.
7. The browser console is clean; the Event Log shows the sign-ins.

## Common mistakes

- **The button is missing.** The per-site provider type is empty; or `Saml2:Enabled` is on but `Saml2:IdpEntityId` or
  `Saml2:IdpMetadataLocation` is empty (then SAML registers nothing); or `WindowsAuth:Enabled` is on a non-Windows host
  without `AllowNonWindows`; or the app was not restarted after changing a switch.
- **`/pages/saml-login` or `/pages/windows-login` answers 403.** The scheme is not registered - same causes as above.
- **Signing in through the provider fails, and the authority is `http://`.** An address the OpenID Connect handler
  would refuse is not used (before 10.73.0 it took every page of the site down). Fix the authority to `https://`. Only
  for a provider on loopback in development, add the site setting `ExternalLogin:RequireHttpsMetadata` = `false`
  through the settings API (it is not on the form).
- **"The External Login Provider Did Not Provide All Of The Required Information."** The identifier claim type does
  not match a claim the bridge received - typically the pre-filled `sub` while the handler delivered it as
  `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier` (step A4). The same message is shown for an
  address outside the **Domain Filter**.
- **The callback fails with "Correlation failed"** (Event Log: "External Login Remote Failure"). The browser dropped
  the handler's `Secure` correlation cookie: the site is served over plain HTTP on a host other than `localhost` /
  `*.localhost`. Serve it over HTTPS.
- **Sign-in succeeds at the provider and comes back to "A User Account Matching The Email Address Of Your External
  Login Does Not Exist. ..."** **Create Users** is off and no account matches; or the e-mail claim type is wrong, so
  the bridge saw no e-mail.
- **Windows sign-in comes back with "A User Account Could Not Be Created For Your External Login. ..."** The default
  name claim is `DOMAIN\user`; set `WindowsAuth__NameClaimType` as in step C1.
- **Users get a new account every time.** The identifier claim type points at a claim that changes per sign-in. (An
  empty or missing identifier claim creates nobody: the sign-in is refused with "The External Login Provider Did Not
  Provide All Of The Required Information".) Use **Review Claims** and pick the provider's stable subject claim.
- **Nobody can sign in through the provider.** **Review Claims** is still on.
- **The SAML or Windows claim types in `appsettings.override.json` have no effect (before 10.73.2).** On an older
  Stomme, move them to `appsettings.json` or environment variables; since 10.73.2 the override file is read too.
- **Someone signed in as another person's account.** **Verify Users** was off and the provider released an unverified
  e-mail. Turn it back on.
- **A Host role appeared on an external user.** **Allow Host Role** was on with a role mapping. Turn it off and remove the
  role in **Admin > User Management**.
- **The redirect URI does not match.** A virtual-folder site's callback includes the alias path; behind a proxy the host
  must receive the public host name and `X-Forwarded-Proto: https`.
- **A return URL that is not a local path is dropped.** A `returnurl` such as `//other.example` sends the person to `/`
  after signing in, not to where the link meant: build it yourself from a local path of the site (see
  [authentication-options.md](../../../docs/security/authentication-options.md)).
