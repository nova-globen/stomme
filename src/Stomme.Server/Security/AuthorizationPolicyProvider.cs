using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Stomme.Infrastructure.Security;
using Stomme.Shared;
using System.Threading.Tasks;

namespace Stomme.Security
{
    public class AuthorizationPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        private readonly AuthorizationOptions _options;

        public AuthorizationPolicyProvider(IOptions<AuthorizationOptions> options) : base(options)
        {
            _options = options.Value;
        }

        public override async Task<AuthorizationPolicy> GetPolicyAsync(string policyName)
        {
            // get policy
            policyName = GetPolicyName(policyName);
            var policy = await base.GetPolicyAsync(policyName);

            if (policy == null)
            {
                // D99 point 9 (BL-99): a LIFTED PERMISSION KEY, "key:resource:operation". Checked FIRST and
                // returned here, because the 3-segment branch below would otherwise SWALLOW it — measured:
                // "key:gatetest:manage" splits into three segments and becomes
                // PermissionRequirement(EntityName = "key", PermissionName = "gatetest", Roles = "manage"),
                // i.e. an entity that has no rows anywhere, decided by the roles fallback on a role named
                // "manage". A key is decided ONLY by its EntityId = -1 rows, so it gets its own requirement.
                if (RequiresPermissionKeyAttribute.TryGetKey(policyName, out var key))
                {
                    var keyBuilder = new AuthorizationPolicyBuilder();
                    keyBuilder.AddRequirements(new PermissionKeyRequirement(key));
                    policy = keyBuilder.Build();

                    try
                    {
                        _options.AddPolicy(policyName, policy);
                    }
                    catch
                    {
                        // race condition - policy already added by another thread
                    }

                    return policy;
                }

                // policy names must be in the form of "EntityName:PermissionName:Roles"
                if (policyName.Contains(':'))
                {
                    var segments = policyName.Split(':');
                    if (segments.Length == 3)
                    {
                        // create policy
                        var builder = new AuthorizationPolicyBuilder();
                        builder.AddRequirements(new PermissionRequirement(segments[0], segments[1], segments[2]));
                        policy = builder.Build();

                        // add policy to the AuthorizationOptions
                        try
                        {
                            _options.AddPolicy(policyName, policy);
                        }
                        catch
                        {
                            // race condition - policy already added by another thread
                        }
                    }
                }
            }

            return policy;
        }

        private string GetPolicyName(string policyName)
        {
            // backward compatibility for legacy static policy names
            if (policyName == PolicyNames.ViewModule) policyName = $"{EntityNames.Module}:{PermissionNames.View}:{RoleNames.Admin}";
            if (policyName == PolicyNames.EditModule) policyName = $"{EntityNames.Module}:{PermissionNames.Edit}:{RoleNames.Admin}";
            return policyName;
        }
    }
}
