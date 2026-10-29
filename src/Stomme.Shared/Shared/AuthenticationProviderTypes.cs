namespace Stomme.Shared {
    public class AuthenticationProviderTypes {
        public const string OpenIDConnect = "oidc";
        public const string OAuth2 = "oauth2";
        // Installation-wide, config-gated external-login consumers. These strings are the UserLogin
        // provider-type discriminator (linkage stays PER-SITE — the bridge appends ":{siteId}"), NOT the
        // ASP.NET authentication scheme names (those are the library defaults "Saml2"/"Negotiate").
        public const string Saml2 = "saml2";
        public const string Negotiate = "negotiate";
    }
}
