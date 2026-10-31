---
name: stomme-oidc-provider
description: Use when a Nova Globen Stomme installation must act as an OpenID Connect / OAuth 2.0 provider - single sign-on for other applications, access tokens for calling the site's API from a script, a service, a mobile app or a TV (the device authorization grant), client-credentials service accounts, registering client applications, or setting the signing certificate - or when tokens stop working after a restart, are rejected as ID2088, or a discovery document answers 404.
---

# Run the OpenID Connect provider

You will have an installation that issues OpenID Connect tokens per tenant host name: other applications sign people in
with their Stomme account (authorization code with PKCE), and a script or service calls the site's `/api/...` with an
access token. Read [authentication-options.md](../../../docs/security/authentication-options.md) first - it explains the
per-site issuer, the endpoints and every key.

## Steps

1. **Know each site's issuer.** A site at a host name issues at `https://{host}`; a site in a folder issues at
   `https://{host}/{folder}` (since 10.76.0), with its endpoints under `/{folder}/connect/` and its own users. A site
   reachable at both has two issuers; a token works at the one that issued it.

2. **Make a signing certificate and an encryption certificate** (a self-signed RSA certificate is enough; the public
   key is published in the JWKS). Example with OpenSSL, run where the app's data lives:

   ```
   openssl req -x509 -newkey rsa:3072 -sha256 -days 730 -nodes -subj "/CN=app.example.com token signing" -keyout signing.key -out signing.crt
   openssl req -x509 -newkey rsa:3072 -sha256 -days 730 -nodes -subj "/CN=app.example.com token encryption" -keyout encryption.key -out encryption.crt
   ```

   Keep the files out of the repository, on storage that survives a redeploy, readable only by the app. (In Git Bash
   on Windows, prefix each command with `MSYS_NO_PATHCONV=1`, or `/CN=...` is rewritten into a file path; PowerShell
   and Linux shells need nothing.)

3. **Switch the provider on and point it at the certificates.** Environment variables (or the installation's
   `appsettings.override.json`; never the committed `appsettings.json` for paths and passwords):

   ```
   OpenIddict__Enabled=true
   OpenIddict__SigningCertificatePemPath=/data/oidc/signing.crt
   OpenIddict__SigningCertificateKeyPath=/data/oidc/signing.key
   OpenIddict__EncryptionCertificatePemPath=/data/oidc/encryption.crt
   OpenIddict__EncryptionCertificateKeyPath=/data/oidc/encryption.key
   ```

   The same keys as JSON in the installation's `appsettings.override.json` work too:
   `"OpenIddict": { "Enabled": true, "SigningCertificatePemPath": "...", ... }`. A `.pfx` works too:
   `OpenIddict__SigningCertificatePath` and `OpenIddict__SigningCertificatePassword` (and the `Encryption...` pair). A
   configured path that does not exist stops the app at startup with the key's name.

4. **Restart the app.** The switch is read at startup. **Admin > Identity Provider** (`/admin/openiddict`, Host only)
   appears.

5. **Register a client application**, either in **Admin > Identity Provider** (the secret is shown once, in the answer
   to the create; it is hashed at rest), or declaratively, created or updated per tenant on the tenant's first request
   after each start, and never deleted:

   ```json
   "OpenIddict": {
     "Applications": [
       {
         "ClientId": "reports",
         "ClientSecretEnv": "REPORTS_CLIENT_SECRET",
         "DisplayName": "Reports",
         "Type": "confidential",
         "GrantTypes": [ "authorization_code", "refresh_token" ],
         "Scopes": [ "openid", "profile", "email", "offline_access", "api" ],
         "RedirectUris": [ "https://reports.example.com/signin-oidc" ],
         "PostLogoutRedirectUris": [ "https://reports.example.com/signout-callback-oidc" ]
       }
     ]
   }
   ```

   `ClientSecretEnv` names an environment variable holding the secret, so the secret never enters a file; the variable
   must be set in the app's own environment. A browser or mobile app is `"Type": "public"` with no secret. PKCE is
   required for every authorization-code request, confidential clients included (a request without `code_challenge`
   is refused with "The mandatory 'code_challenge' parameter is missing."), and the method is `S256` only: discovery
   lists `code_challenge_methods_supported: ["S256"]` and `code_challenge_method=plain` is refused with `invalid_request`. A client gets only the endpoints, grants
   and scopes listed. Include `api` in `Scopes` for a client that calls the site's API. A changed item (a new redirect
   URI, for example) is applied at the next start.

   A **native app** (Android, iOS, MAUI, desktop) is a public client with a private-use-scheme or loopback redirect, limited
   to the site it belongs to with `Sites`:

   ```json
   {
     "ClientId": "shop-app",
     "Type": "public",
     "GrantTypes": [ "authorization_code", "refresh_token" ],
     "Scopes": [ "openid", "profile", "offline_access", "api" ],
     "RedirectUris": [ "com.example.shop:/oauth2redirect", "http://127.0.0.1/callback" ],
     "Sites": [ "example.com/shop" ]
   }
   ```

   The app uses a standard OIDC client (AppAuth, IdentityModel.OidcClient) with the issuer `https://example.com/shop`. A
   loopback redirect matches on any port (`http://127.0.0.1:53917/callback`); `localhost` does not. On sign-out the app
   revokes its refresh token (`POST /shop/connect/revoke` with `token`, `token_type_hint=refresh_token`, `client_id`).
   With `Sites` the client is refused (`unauthorized_client`) at every other site, even one sharing its tenant. Never
   give an app a password-for-token endpoint of a module's own.

6. **Point the other application at the site's issuer** (`https://{host}` or `https://{host}/{folder}`): it reads
   `{issuer}/.well-known/openid-configuration`. Use authorization code with PKCE. The ID token carries `sub`, `name`,
   `preferred_username`, `email` and `site` (the issuing site's alias); roles are not in the token.

7. **Call the site's API with a token** (for a person, from step 6; the token must carry the `api` scope):

   ```
   GET https://{tenant host}/api/{Controller}/...
   Authorization: Bearer {access token}
   ```

   The request runs as that person on the REQUEST's site, with their roles and permission keys read per request. No
   antiforgery header is needed with a bearer token.

8. **A service with no person behind it** (client credentials): register a confidential client with
   `"GrantTypes": [ "client_credentials" ]` and `"Scopes": [ "api" ]`, set `OpenIddict__ServiceAccounts__Enabled=true`,
   restart, and create the client's service account from its page in **Admin > Identity Provider** (or
   `POST api/OpenIddict/applications/{clientId}/service-account` as the Host). The account is created on the site you
   are on, already in `Registered Users`; grant it further roles in **Admin > User Management** as for a person.
   Without a service account a client-credentials token signs nobody in on `/api/...`.
9. **A device without a browser** (a TV app, since 10.77.0): register a `public` client with
   `"GrantTypes": [ "device_code", "refresh_token" ]` and `"Scopes": [ "openid", "profile", "offline_access", "api" ]`
   (add `"Sites"` to keep it to one site). The device POSTs `client_id` and `scope` to `{issuer}/connect/device`, shows
   the `user_code` and `verification_uri` (or a QR code of `verification_uri_complete`), and polls `{issuer}/connect/token`
   with `grant_type=urn:ietf:params:oauth:grant-type:device_code`, `device_code` and `client_id` every `interval` seconds
   until it gets tokens or `access_denied`; `slow_down` means poll less often. The person approves on the site's
   `/connect/verify` after signing in. `OpenIddict:DevicePollingIntervalSeconds` sets the interval (default 5).

## Verify

Everything below was driven on a local install (SQLite, Development, plain HTTP on loopback host names); in
production use `https://`.

1. `GET https://{tenant host}/.well-known/openid-configuration` answers JSON whose `issuer` is `https://{tenant host}/`
   and whose `jwks_uri` is `/.well-known/jwks`; the same request on another host name of the installation shows that
   host as the issuer. `GET /.well-known/jwks` lists one `RSA` key with `use: sig`.
2. Client credentials, from a shell (`nightly-sync` being a client registered with the `client_credentials` grant and
   the `api` scope, as in step 8):

   ```
   curl -s -X POST https://{tenant host}/connect/token -d grant_type=client_credentials -d client_id=nightly-sync -d client_secret=... -d scope=api
   ```

   answers an `access_token` (`token_type` `Bearer`, `expires_in` 3599) whose payload has `aud` `stomme-api`,
   `scope` `api` and `sub` = the client id. A wrong secret answers 401 `invalid_client`. `GET /connect/whoami` with the
   token answers `{"subject":"nightly-sync","scopes":["api"]}`; `GET /connect/userinfo` answers 401 (there is no
   person). With the service account of step 8, `GET /api/{Controller}` answers 200, and a `POST` works without an
   antiforgery header.
3. Authorization code: `GET /connect/authorize?client_id=...&redirect_uri=...&response_type=code&scope=openid profile email api offline_access&code_challenge=...&code_challenge_method=S256&state=...&nonce=...`
   - not signed in: 302 to `/login?returnurl=/connect/authorize...`;
   - signed in: 302 to the redirect URI with `code`, `state` and `iss` (or a self-posting form with
     `response_mode=form_post`).
   Exchanging the code (with `code_verifier`) at `/connect/token` answers `access_token`, `id_token` and, with
   `offline_access`, `refresh_token`. The ID token carries `sub` (the user's identity id), `name`,
   `preferred_username` and `email`, no roles. `/connect/userinfo` with the access token answers the same claims and,
   for a token with the `profile` scope, `role`: the user's role names on the issuer's site, an array (since 10.75.0;
   a Host holds `Host`, `Administrators` and `Registered Users`).
   The refresh token gets new tokens. `GET /connect/endsession?id_token_hint=...&post_logout_redirect_uri=...`
   answers 302 to the registered post-logout URI, and the next authorize request asks for the login again.
4. Sign in to the other application through Stomme (another Stomme site can be that application - the
   [stomme-add-external-login](../stomme-add-external-login/SKILL.md) skill): the Stomme login page appears, then
   the application receives the user.
5. `GET /api/...` with the person's access token answers what that person may see; with no token the same call is
   anonymous; with a token from another site - another host name, the root of a folder site's host, or another folder -
   it is anonymous (the token is rejected; a call that needs a permission answers 403).
6. Restart the app: the token from step 2 still works and the JWKS `kid` is unchanged (a certificate is configured).
   Delete the client in **Admin > Identity Provider** (or `DELETE api/OpenIddict/applications/{clientId}`) and call
   `/api/...` with its old token: it is anonymous at once (`/connect/whoami` answers 401). A client declared in
   `OpenIddict:Applications` comes back at the next start.
7. A device client (step 9): `POST {issuer}/connect/device` answers `device_code`, `user_code`, `verification_uri` and
   `interval`; polling `/connect/token` answers `authorization_pending`; after approving on `{issuer}/connect/verify` the
   next poll answers tokens whose `site` claim is the issuing site. The same code entered on another site's verify page
   is refused ("belongs to another site"), and a client without the `device_code` grant is refused at `/connect/device`.

## Common mistakes

- **Every restart invalidates every token, and the JWKS changes.** No signing certificate is configured: the provider
  fell back to an ephemeral key. Step 2 and 3.
- **Discovery answers 404.** `OpenIddict:Enabled` is not `true` in the configuration the host runs on, or the app was not
  restarted. On 10.75 and earlier a site reachable only at a folder had no provider.
- **A folder site's app gets `unauthorized_client`.** The client lists `Sites` and the request went to another site - most
  often the host's root issuer instead of `https://{host}/{folder}`.
- **The authorize request answers `invalid_request` for the redirect URI.** A loopback redirect must be an IP literal
  (`127.0.0.1` or `[::1]`), with the registered path and query; only the port may differ.
- **The provider refuses plain HTTP outside Development** - every endpoint, discovery included, answers 400
  "This server only accepts HTTPS requests.". Serve HTTPS, or have the reverse proxy send `X-Forwarded-Proto: https`.
  The host believes that header only from a proxy on loopback or a private network, or one named in
  `ForwardedHeaders:KnownProxies` (see [security-posture.md](../../../docs/security/security-posture.md)); a proxy
  elsewhere must be named, or the provider sees plain HTTP.
- **"The mandatory 'code_challenge' parameter is missing."** The client does not send PKCE; every authorization-code
  request needs it, a confidential client's too.
- **`invalid_request` on authorize with `code_challenge_method=plain`.** Only `S256` is accepted (RFC 7636, RFC 8252); send
  the SHA-256 of the verifier, base64url-encoded, with `code_challenge_method=S256`.
- **A token works on one host name and not on another.** By design: the issuer is the host name. Use the host name the
  token was issued at.
- **The API ignores the token.** It lacks the `api` scope (the client was not registered with it, or did not request
  it), or it is a client-credentials token and no service account exists.
- **`invalid_client` for a configured client.** It is confidential and its secret resolved to nothing - the variable
  named by `ClientSecretEnv` is not set in the app's environment.
- **No consent screen appears.** The client is `implicit` (the default), which never asks; set `ConsentType = explicit`
  for a third-party client (Stomme 10.79.0 or later). A user who allowed it once is not asked again for the same scopes.
- **Several instances disagree about tokens.** Every instance needs the same certificates (and the same Data Protection
  key ring - see [security-posture.md](../../../docs/security/security-posture.md)).
