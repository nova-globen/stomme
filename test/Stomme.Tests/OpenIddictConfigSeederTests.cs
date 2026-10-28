using System;
using System.Linq;
using Stomme.Identity.OpenIddict;
using Stomme.Models;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Stomme.Tests
{
    /// <summary>
    /// Client/scope work (a) — the config seeder's descriptor mapping. These are the error-prone part (grants →
    /// permissions, scopes → scope permissions, PKCE, secret resolution) and are pinned deterministically
    /// without a database. The storage-blind round-trip through a real store is
    /// <see cref="OpenIddictClientManagementTests"/>.
    /// </summary>
    public class OpenIddictConfigSeederTests
    {
        [Fact]
        public void Confidential_authcode_client_gets_the_right_permissions()
        {
            var descriptor = OpenIddictConfigSeeder.BuildDescriptor(new OpenIddictApplicationSeed
            {
                ClientId = "web",
                ClientSecret = "s3cret",
                GrantTypes = { "authorization_code", "refresh_token" },
                Scopes = { "openid", "profile", "email", "offline_access", "api" },
                RedirectUris = { "https://app.example/signin-oidc" },
                PostLogoutRedirectUris = { "https://app.example/signout-callback-oidc" }
            });

            Assert.Equal(ClientTypes.Confidential, descriptor.ClientType);
            Assert.Equal("s3cret", descriptor.ClientSecret);
            Assert.Contains(Permissions.Endpoints.Token, descriptor.Permissions);
            Assert.Contains(Permissions.Endpoints.Authorization, descriptor.Permissions);
            Assert.Contains(Permissions.Endpoints.EndSession, descriptor.Permissions);
            Assert.Contains(Permissions.GrantTypes.AuthorizationCode, descriptor.Permissions);
            Assert.Contains(Permissions.GrantTypes.RefreshToken, descriptor.Permissions);
            Assert.Contains(Permissions.ResponseTypes.Code, descriptor.Permissions);
            Assert.Contains(Permissions.Prefixes.Scope + "offline_access", descriptor.Permissions);
            Assert.Contains(Permissions.Prefixes.Scope + "api", descriptor.Permissions);
            Assert.Contains(Requirements.Features.ProofKeyForCodeExchange, descriptor.Requirements);
            Assert.Contains(new Uri("https://app.example/signin-oidc"), descriptor.RedirectUris);
            Assert.Contains(new Uri("https://app.example/signout-callback-oidc"), descriptor.PostLogoutRedirectUris);
            // A client that did not ask for client_credentials must not be granted it.
            Assert.DoesNotContain(Permissions.GrantTypes.ClientCredentials, descriptor.Permissions);
        }

        [Fact]
        public void Client_credentials_only_client_has_no_authcode_or_pkce()
        {
            var descriptor = OpenIddictConfigSeeder.BuildDescriptor(new OpenIddictApplicationSeed
            {
                ClientId = "service",
                ClientSecret = "s3cret",
                GrantTypes = { "client_credentials" },
                Scopes = { "api" }
            });

            Assert.Contains(Permissions.GrantTypes.ClientCredentials, descriptor.Permissions);
            Assert.DoesNotContain(Permissions.GrantTypes.AuthorizationCode, descriptor.Permissions);
            Assert.DoesNotContain(Permissions.Endpoints.Authorization, descriptor.Permissions);
            Assert.DoesNotContain(Requirements.Features.ProofKeyForCodeExchange, descriptor.Requirements);
            Assert.DoesNotContain(Permissions.Endpoints.EndSession, descriptor.Permissions);
        }

        [Fact]
        public void Public_client_carries_no_secret_and_forces_pkce()
        {
            var descriptor = OpenIddictConfigSeeder.BuildDescriptor(new OpenIddictApplicationSeed
            {
                ClientId = "spa",
                Type = "public",
                RequirePkce = false, // ignored for public — PKCE is mandatory
                GrantTypes = { "authorization_code" },
                Scopes = { "openid" },
                RedirectUris = { "https://spa.example/callback" }
            });

            Assert.Equal(ClientTypes.Public, descriptor.ClientType);
            Assert.Null(descriptor.ClientSecret);
            Assert.Contains(Requirements.Features.ProofKeyForCodeExchange, descriptor.Requirements);
        }

        [Fact]
        public void Confidential_client_can_opt_out_of_pkce()
        {
            var descriptor = OpenIddictConfigSeeder.BuildDescriptor(new OpenIddictApplicationSeed
            {
                ClientId = "legacy",
                ClientSecret = "s3cret",
                RequirePkce = false,
                GrantTypes = { "authorization_code" },
                Scopes = { "openid" },
                RedirectUris = { "https://legacy.example/cb" }
            });

            Assert.DoesNotContain(Requirements.Features.ProofKeyForCodeExchange, descriptor.Requirements);
        }

        [Fact]
        public void Confidential_client_without_a_secret_is_rejected()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                OpenIddictConfigSeeder.BuildDescriptor(new OpenIddictApplicationSeed
                {
                    ClientId = "web",
                    GrantTypes = { "authorization_code" }
                }));
            Assert.Contains("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Secret_is_resolved_from_an_environment_variable_when_named()
        {
            var envName = "STOMME_TEST_OIDC_SECRET_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(envName, "from-env");
            try
            {
                var seed = new OpenIddictApplicationSeed
                {
                    ClientId = "web",
                    ClientSecret = "from-file",
                    ClientSecretEnv = envName,
                    GrantTypes = { "client_credentials" }
                };
                Assert.Equal("from-env", seed.ResolveSecret());

                var descriptor = OpenIddictConfigSeeder.BuildDescriptor(seed);
                Assert.Equal("from-env", descriptor.ClientSecret); // env wins over the literal
            }
            finally
            {
                Environment.SetEnvironmentVariable(envName, null);
            }
        }

        [Fact]
        public void Malformed_redirect_uris_are_dropped_not_thrown()
        {
            var descriptor = OpenIddictConfigSeeder.BuildDescriptor(new OpenIddictApplicationSeed
            {
                ClientId = "web",
                ClientSecret = "s",
                GrantTypes = { "authorization_code" },
                RedirectUris = { "not a uri", "https://ok.example/cb" }
            });

            Assert.Single(descriptor.RedirectUris);
            Assert.Contains(new Uri("https://ok.example/cb"), descriptor.RedirectUris);
        }
    }
}
