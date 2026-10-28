# Authentication options

Every Nova Globen Stomme site signs users in with a user name and password (ASP.NET Identity) by default; a site's Host can add
passkeys (`LoginOptions:Passkeys`) and e-mailed login links (`LoginOptions:LoginLink`), or hide the password form
(`LoginOptions:AllowSiteLogin`). On top of that a site can sign
users in through an external identity provider (OpenID Connect or OAuth 2.0, set per site), the installation can accept
SAML 2.0 and Windows (Negotiate) sign-in, and the installation can itself be an OpenID Connect provider for other
applications. Everything beyond the password sign-in is off until it is switched on.

## How it works

### The schemes and the one rule

The default authentication scheme is always Stomme's Identity cookie. Every other scheme is challenged only
explicitly, from its own sign-in page: `/pages/external` (the site's OpenID Connect or OAuth 2.0 provider),
`/pages/saml-login` and `/pages/windows-login`. The login page shows a button for each one that is on. No scheme is ever
a default or global challenge; a global Negotiate challenge, for example, would demand Windows sign-in on every request
and stop anonymous pages from rendering.

### The link-or-create bridge

Every external sign-in ends in the same step. Given the provider's identifier, name and e-mail claims, Stomme:

1. finds a user already linked to this provider on this site (the login provider is stored as
   `{providerType}:{siteId}`, so a link is per site) and signs them in, updating their e-mail and display name;
2. otherwise, finds an account with the same e-mail address:
   - links it when the account already has a login of the same provider type on any site, or when
     `ExternalLogin:VerifyUsers` is `false`;
   - otherwise, with `ExternalLogin:VerifyUsers` on (the default), e-mails the account a link that completes the
     linkage, and does not sign in yet;
   - refuses when several accounts share the address;
3. otherwise creates an account when `ExternalLogin:CreateUsers` is on (the default), with a random password nobody is
   told, and links it;
4. gives a user who is new to the site its auto-assigned roles (`Registered Users`), maps role claims when
   `ExternalLogin:RoleClaimType` is set, refuses a locked-out account, and issues the Identity cookie.

Roles always come from Stomme's own rows for the site, read per request; a provider's role claims only add role
memberships when you map them.

### The site's OpenID Connect or OAuth 2.0 provider

One external provider per site, configured by the Host in **Admin > User Management > Settings > External Login
Settings** and stored as site settings. The form shows the **Redirect Url** to register at the provider: the
callback path is `/signin-oidc` (OpenID Connect) or `/signin-oauth2` (OAuth 2.0), prefixed by the alias path for a
virtual-folder alias. The client secret is stored as a private site setting (in clear text in the tenant's database,
hidden from everyone but administrators).

The OpenID Connect handler maps the ID token's claims to .NET claim types before the bridge reads them
(`ExternalLogin:MapInboundClaims`, default `true`): `sub` arrives as
`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`, `email` as
`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress`, and `name` stays `name`. The form pre-fills the
claim types `sub`, `name` and `email`; `sub` and `email` therefore do NOT match: the identifier is not found and the sign-in ends
on the login page with "The External Login Provider Did Not Provide All Of The Required Information". Use **Review
Claims** to see the claim types as the bridge receives them (after this mapping), and enter those.

The handler's correlation and nonce cookies are `Secure`. A browser keeps them over HTTPS, and over plain HTTP only on
`localhost` and `*.localhost`; anywhere else on plain HTTP the callback fails with "Correlation failed" (logged as an
external login remote failure).

### SAML 2.0

Installation-wide, through Sustainsys.Saml2. Registered at startup only when `Saml2:Enabled` is `true` AND
`Saml2:IdpEntityId` and `Saml2:IdpMetadataLocation` are set; enabled but incomplete, it registers nothing. The
assertion is signed into a five-minute external cookie, then `/signin-saml2` runs the bridge and swaps it for the
Identity cookie. The linkage is per site (`saml2:{siteId}`).

### Windows (Negotiate)

Installation-wide. Registered when `WindowsAuth:Enabled` is `true` and the host runs on Windows, or on another OS when
`WindowsAuth:AllowNonWindows` is `true` (a Kerberos-configured Linux host). `/pages/windows-login` challenges Negotiate
(an anonymous request answers 401 with `WWW-Authenticate: Negotiate`), then runs the bridge (`negotiate:{siteId}`).

A Windows identity carries no e-mail claim, and its name is `DOMAIN\user`. With the default claim types the bridge
makes the new account's user name from that name, ASP.NET Identity refuses the backslash, and the sign-in ends with
"user not created" (driven). Set `WindowsAuth:NameClaimType` to a claim without a backslash - for example
`http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid` (driven: the account is then created, user name
and display name derived from the SID, and later sign-ins reuse it). Set it as an environment variable
(`WindowsAuth__NameClaimType`), in `appsettings.json` or in `appsettings.override.json` (since 10.73.2; before, the
override file's value was ignored).

### The OpenID Connect provider (OpenIddict)

With `OpenIddict:Enabled` the installation is an OpenID Connect provider - an authority - for other applications:

- Endpoints: `/connect/authorize`, `/connect/token`, `/connect/userinfo`, `/connect/endsession`, plus
  `/.well-known/openid-configuration` and `/.well-known/jwks`; and `/connect/whoami`, which answers an access token
  issued here with its subject and scopes.
- Grants: authorization code with PKCE (PKCE is required server-wide), client credentials, refresh token. Scopes:
  `openid`, `email`, `profile`, `offline_access`, `api`.
- **Per tenant, per host name.** No issuer is configured; it is inferred from the request's host name, and clients,
  authorizations and tokens live in a store of their own per tenant (never the tenant's main database). A token issued at
  one host name is rejected at another. The host name is the one the request carries after forwarded headers - an
  `X-Forwarded-Host` from the site's proxy changes the issuer, one from anyone else is ignored (see
  [security-posture.md](security-posture.md)).
- The endpoints are mapped at the root of the host. **A site reachable only at a virtual-folder alias
  (`example.com/shop`) has no authority of its own**; give it a host name if it must issue tokens.
- **Calling Stomme's API with a token.** A request to `/api/...` with `Authorization: Bearer {access token}` is signed
  in as the Stomme user the token's subject names on the REQUEST's site, when the token carries the `api` scope and
  the `stomme-api` audience. Roles and permissions are read per request, not from the token. A revoked token stops
  working at once. A client-credentials token signs nobody in, unless service accounts are on (below).
- **Service accounts.** With `OpenIddict:ServiceAccounts:Enabled` a Host creates, from the client's page, a Stomme
  user for a confidential client-credentials application that holds the `api` scope (`svc-` + the client id lower-cased, other characters
  than letters, digits, `-`, `_` and `.` made `-`, at most 40 of them, + `-` + six hex characters; locked
  out of interactive sign-in). It starts with `Registered Users` on the site it was created on; an Administrator grants
  it further roles like a person; its tokens then sign it in on `/api/...` (driven: `GET` and `POST api/Notes` with a
  client-credentials token, no antiforgery header).
- The Host manages clients and scopes in **Admin > Identity Provider** (`/admin/openiddict`, Host only, added
  at the first start with the switch on; switching it off later leaves the page, which then says the provider is
  disabled), or declares them in configuration (`OpenIddict:Applications`, `OpenIddict:Scopes`), which are created or
  updated per tenant on the tenant's first request after each start and never deleted.

Step by step: the [stomme-oidc-provider](../../.claude/skills/stomme-oidc-provider/SKILL.md) skill.

### Tokens for the site's own API (site JWT)

Separately, a site can accept its own HS256 JWTs on `/api/...`: when the site setting `JwtOptions:Secret` is set
(**Admin > User Management > Settings > Token Settings**), a bearer token signed with it, carrying the user id and name,
signs that user in. Off while the secret is empty.

## Using it from an app

Nothing in an app's code changes per scheme: every scheme ends in the same Identity cookie and the same Stomme user,
so `ICurrentUser`, `[RequiresPermissionKey]`, `[Requires]` and `IsAuthorizedPermission` work unchanged (see
[identity-and-permissions.md](identity-and-permissions.md)).

To send a person to a scheme from your own page, link to its sign-in page with a local return URL:

```razor
<a class="db-btn" href="@Utilities.TenantUrl(PageState.Alias, "/pages/external?returnurl=" + System.Net.WebUtility.UrlEncode("/" + PageState.Page.Path))">Sign in with your company account</a>
```

(`Stomme.Shared.Utilities.TenantUrl` prefixes the alias path for a virtual-folder site.)

## Configuration

Installation-wide switches are read once, at startup, from the merged configuration (`appsettings.json`, the
environment's file, `features.json`, environment variables, then `appsettings.override.json`). Change them and restart.

| Key | Default | Effect |
|---|---|---|
| `OpenIddict:Enabled` | `false` | The OpenID Connect provider, its endpoints, its admin page and bearer tokens on `/api/...` |
| `OpenIddict:ServiceAccounts:Enabled` | `false` | Service accounts for client-credentials applications (only with the provider on) |
| `OpenIddict:SigningCertificatePath` / `OpenIddict:SigningCertificatePassword` | none | A PKCS#12 signing certificate. Without one, an ephemeral key: every restart invalidates every token and changes the JWKS |
| `OpenIddict:SigningCertificatePemPath` / `OpenIddict:SigningCertificateKeyPath` | none | The same, as PEM certificate and key (both required) |
| `OpenIddict:EncryptionCertificatePath` / `...Password` / `...PemPath` / `...KeyPath` | none | The certificate that protects authorization codes; ephemeral without one |
| `OpenIddict:Applications` | empty | Client applications to create or update (shape below) |
| `OpenIddict:Scopes` | empty | Custom scopes: `Name`, `DisplayName`, `Description`, `Resources` |
| `Saml2:Enabled` | `false` | SAML 2.0 sign-in (also needs the two keys below) |
| `Saml2:IdpEntityId` | none | The identity provider's entity id |
| `Saml2:IdpMetadataLocation` | none | Its metadata URL or file |
| `Saml2:SpEntityId` | `https://stomme/saml` | This service provider's entity id - set your own |
| `Saml2:PublicOrigin` | none | The public origin when behind a proxy |
| `Saml2:AllowUnsolicited` | `true` | Accept IdP-initiated responses |
| `Saml2:ProviderName` | `SAML` | The button's label |
| `Saml2:IdentifierClaimType` / `NameClaimType` / `EmailClaimType` | `ClaimTypes.NameIdentifier` / `.Name` / `.Email` | Which assertion claims the bridge reads |
| `WindowsAuth:Enabled` | `false` | Windows (Negotiate) sign-in |
| `WindowsAuth:AllowNonWindows` | `false` | Register Negotiate on a non-Windows host |
| `WindowsAuth:ProviderName` | `Windows` | The button's label |
| `WindowsAuth:IdentifierClaimType` / `NameClaimType` / `EmailClaimType` | `ClaimTypes.Name` / `.Name` / `.Email` | Which claims the bridge reads |

An `OpenIddict:Applications` item (`Stomme.Models.OpenIddictApplicationSeed`): `ClientId` (required), `ClientSecret`
or `ClientSecretEnv` (the name of an environment variable holding it; wins over the literal), `DisplayName`, `Type`
(`confidential`, the default, or `public`), `ConsentType`, `GrantTypes` (`authorization_code`, `client_credentials`,
`refresh_token`), `Scopes`, `RedirectUris`, `PostLogoutRedirectUris`, `RequirePkce` (default `true`).

Per-site external login (site settings, **Admin > User Management > Settings**, Host only), the main ones:

| Site setting | Default | Effect |
|---|---|---|
| `ExternalLogin:ProviderType` | empty | `oidc` or `oauth2`; empty = no external provider |
| `ExternalLogin:Authority`, `ExternalLogin:MetadataUrl` | empty | OpenID Connect discovery |
| `ExternalLogin:AuthorizationUrl`, `TokenUrl`, `UserInfoUrl` | empty | OAuth 2.0 endpoints |
| `ExternalLogin:ClientId`, `ExternalLogin:ClientSecret` | empty | The client registration |
| `ExternalLogin:Scopes` | `openid,profile,email` (OIDC) | Requested scopes |
| `ExternalLogin:PKCE` | `false` | Use PKCE |
| `ExternalLogin:IdentifierClaimType`, `NameClaimType`, `EmailClaimType` | empty (the form pre-fills `sub`, `name`, `email`) | Claims the bridge reads; see the claim mapping above |
| `ExternalLogin:DomainFilter` | empty | Accept only these e-mail domains (the OpenID Connect / OAuth 2.0 provider only, not SAML or Windows). An address outside them is refused even for an account already linked |
| `ExternalLogin:RequireHttpsMetadata` | `true` | Refuse an `http://` authority. Not on the form; set it through the settings API, and only for a loopback test provider |
| `ExternalLogin:MapInboundClaims` | `true` | Map the token's claim types to .NET claim types (`sub` becomes `...claims/nameidentifier`). Not on the form |
| `ExternalLogin:ResponseMode` | `form_post` | How the provider returns the code. Not on the form |
| `ExternalLogin:CreateUsers` | `true` | Create an account for an unknown user |
| `ExternalLogin:VerifyUsers` | `true` | E-mail an existing account before linking it |
| `ExternalLogin:RoleClaimType`, `RoleClaimMappings`, `SynchronizeRoles`, `AllowHostRole` | empty / empty / `false` / `false` | Role claims to Stomme roles |
| `ExternalLogin:ReviewClaims` | `false` | Log the provider's claims and do NOT sign in (for setting up the claim types) |

## Rules and pitfalls

- **Keep `ExternalLogin:VerifyUsers` on.** Off, an external sign-in whose e-mail matches an existing account is linked to
  it without asking - whoever controls that address at the provider takes the account over.
- **Keep `ExternalLogin:AllowHostRole` off** unless the provider is the installation's own. On, a role claim can grant
  `Host Users`.
- **The role-claim site settings apply to SAML and Windows sign-in too.** The bridge reads `ExternalLogin:RoleClaimType`
  and its mappings for every provider.
- **The `Saml2:*ClaimType` and `WindowsAuth:*ClaimType` keys work from any source since 10.73.2**, the override file
  included. Before, the sign-in pages read them, and the provider name they store on the user's login, from the web
  host's default configuration, which leaves out the override file and `features.json`, so a value there was ignored
  and the defaults applied.
- **An `http://` authority is refused, and not used.** The form refuses an `http://` Authority or Metadata Url, and an
  address saved another way while `ExternalLogin:RequireHttpsMetadata` is `true` is not handed to the OpenID Connect
  handler: signing in through the provider fails, and the rest of the site keeps serving. (Before 10.73.0 such a value
  made every page and API call of the site answer 500, a restart included.)
- **A `returnurl` is a path on this site, or the sign-in pages go home.** `/pages/external`, `/pages/saml-login`,
  `/pages/windows-login`, `/pages/login` and `/login` keep a `returnurl` only when it is a local path - one leading `/`,
  not `//` or `/\` (which a browser reads as another host), no control characters - and otherwise send the person to
  `/`. `Utilities.LocalReturnUrl` (`Stomme.Shared`) applies that rule; use it for any return address your own code
  takes. (Before 10.73.0 these pages accepted `//other.example/x` and sent the person there after signing in.)
- **A production OpenID Connect provider needs a signing certificate.** With the ephemeral fallback every restart and
  every second instance invalidates tokens. One certificate serves every tenant; isolation comes from the issuer and
  the per-tenant store.
- **The provider requires HTTPS outside Development.** Behind a TLS-terminating proxy the host must see
  `X-Forwarded-Proto: https`.
- **`ConsentType = explicit` shows no consent screen.** The authorize endpoint issues a code for a signed-in user
  whatever the client's consent type. Register only clients you trust.
- **Secrets go in environment variables.** Use `ClientSecretEnv` and `OpenIddict__SigningCertificatePassword`, never a
  literal in a committed file.
- **One external provider per site.** A second OpenID Connect provider for the same site is not supported.
- **Two-factor sign-in is an e-mailed code.** Configure mail before setting `LoginOptions:TwoFactor` to `required`, or
  nobody can sign in through the login page. There is no authenticator-app enrolment.

## Related

- [identity-and-permissions.md](identity-and-permissions.md) - users, roles, permission keys
- [tenancy.md](tenancy.md) - why the provider is per host name
- [security-posture.md](security-posture.md) - secrets, forwarded headers, the Data Protection key ring
- [../architecture/configuration.md](../architecture/configuration.md) - where configuration comes from
- [../../.claude/skills/stomme-add-external-login/SKILL.md](../../.claude/skills/stomme-add-external-login/SKILL.md)
- [../../.claude/skills/stomme-oidc-provider/SKILL.md](../../.claude/skills/stomme-oidc-provider/SKILL.md)
