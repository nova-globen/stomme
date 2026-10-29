using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Security;
using Stomme.Shared;
using Stomme.Transport.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// A <c>[RequiresPermissionKey("resource:operation")]</c> policy must actually RESOLVE - and must resolve
    /// to the key requirement, not to the framework's dynamic 3-segment one. Also pins
    /// the hub policies, whose inert <c>RequireClaim("permission", ...)</c> is replaced by the same requirement.
    /// </summary>
    public class PermissionKeyPolicyResolutionTests
    {
        [Fact]
        public async Task A_requires_permission_key_policy_resolves_to_the_key_requirement()
        {
            var provider = new AuthorizationPolicyProvider(Options.Create(new AuthorizationOptions()));
            var attribute = new RequiresPermissionKeyAttribute("gatetest:manage");

            var policy = await provider.GetPolicyAsync(attribute.Policy);

            var requirement = Assert.IsType<PermissionKeyRequirement>(Assert.Single(policy.Requirements));
            Assert.Equal("gatetest", requirement.Resource);
            Assert.Equal("manage", requirement.Operation);
        }

        // The trap this prefix exists for, MEASURED before it was fixed: an un-prefixed key cannot resolve at
        // all (2 segments), and the natural 3-segment spelling "key:gatetest:manage" WAS resolving through the
        // dynamic provider as PermissionRequirement(EntityName = "key", PermissionName = "gatetest",
        // Roles = "manage") - an entity with no rows, decided by the roles fallback on a role named "manage".
        // Red before the key branch was added: "Assert.Empty() Failure: Collection was not empty".
        [Fact]
        public async Task The_three_segment_branch_does_not_claim_a_key_policy()
        {
            var provider = new AuthorizationPolicyProvider(Options.Create(new AuthorizationOptions()));

            var policy = await provider.GetPolicyAsync(RequiresPermissionKeyAttribute.PolicyName("gatetest:manage"));

            Assert.Empty(policy.Requirements.OfType<PermissionRequirement>());
        }

        // ...and the framework's own policies still resolve exactly as they did: the key branch narrows
        // nothing. Without this the test above would be satisfied by a provider that resolves nothing at all.
        [Fact]
        public async Task The_frameworks_own_three_segment_policies_are_untouched()
        {
            var provider = new AuthorizationPolicyProvider(Options.Create(new AuthorizationOptions()));

            var policy = await provider.GetPolicyAsync($"{EntityNames.User}:{PermissionNames.Write}:{RoleNames.Admin}");

            var requirement = Assert.IsType<PermissionRequirement>(Assert.Single(policy.Requirements));
            Assert.Equal(EntityNames.User, requirement.EntityName);
            Assert.Equal(PermissionNames.Write, requirement.PermissionName);
            Assert.Equal(RoleNames.Admin, requirement.Roles);

            // The legacy static names keep resolving too (ViewModule -> Module:View:Administrators).
            var legacy = await provider.GetPolicyAsync(PolicyNames.ViewModule);
            Assert.IsType<PermissionRequirement>(Assert.Single(legacy.Requirements));
        }

        [Fact]
        public void The_attribute_carries_the_prefixed_policy_name_and_nothing_else()
        {
            var attribute = new RequiresPermissionKeyAttribute("pipelines:manage");

            Assert.Equal("key:pipelines:manage", attribute.Policy);
            Assert.Equal("pipelines:manage", attribute.Key);
            Assert.Null(attribute.Roles);

            Assert.True(RequiresPermissionKeyAttribute.TryGetKey("key:pipelines:manage", out var key));
            Assert.Equal("pipelines:manage", key);
            Assert.False(RequiresPermissionKeyAttribute.TryGetKey("User:Write:Administrators", out _));
            Assert.False(RequiresPermissionKeyAttribute.TryGetKey("key:", out _));
        }

        [Fact]
        public void A_hub_policy_declaring_a_permission_gates_on_the_key_rows_not_on_an_unissued_claim()
        {
            var builder = new AuthorizationPolicyBuilder();
            TransportServiceCollectionExtensions.ConfigureHubPolicy(builder, "gatetest:manage");
            var policy = builder.Build();

            var requirement = Assert.Single(policy.Requirements.OfType<PermissionKeyRequirement>());
            Assert.Equal("gatetest", requirement.Resource);
            Assert.Equal("manage", requirement.Operation);

            // Stomme issues NO "permission" claim (UserSecurity.CreateClaimsIdentity mints name, id, site
            // key, roles and the security stamp - nothing else), so the replaced ClaimsAuthorizationRequirement
            // could never be satisfied: a hub declaring a permission would have rejected EVERY connection.
            Assert.Empty(policy.Requirements.OfType<ClaimsAuthorizationRequirement>());
            Assert.Single(policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>());
        }

        [Fact]
        public void A_hub_policy_without_a_permission_is_authenticated_only_exactly_as_it_shipped()
        {
            var builder = new AuthorizationPolicyBuilder();
            TransportServiceCollectionExtensions.ConfigureHubPolicy(builder, null);
            var policy = builder.Build();

            Assert.Single(policy.Requirements);
            Assert.Single(policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>());

            // ...which is what the NOTIFICATION hub ships with (it targets the recipient user). The HEALTH hub is its page's
            // own key since it began carrying that page's live update: a connection is opened under the same key the page
            // itself checks.
            Assert.Null(HubPolicies.All.Single(entry => entry.Name == HubPolicies.Notification).Permission);
            Assert.Equal(Stomme.Health.HealthPermissions.View, HubPolicies.All.Single(entry => entry.Name == HubPolicies.Health).Permission);
        }

        [Fact]
        public void The_registered_hub_policies_are_the_ones_the_transport_declares()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string>
                {
                    ["Transport:SignalR:Enabled"] = "true"
                })
                .Build();

            services.AddStommeTransport(configuration);

            var options = services.BuildServiceProvider().GetRequiredService<IOptions<AuthorizationOptions>>().Value;
            foreach (var (name, _) in HubPolicies.All)
            {
                var policy = options.GetPolicy(name);
                Assert.NotNull(policy);
                Assert.Single(policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>());
                Assert.Empty(policy.Requirements.OfType<ClaimsAuthorizationRequirement>());
            }
        }
    }
}