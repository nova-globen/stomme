using System;
using Microsoft.AspNetCore.Authorization;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// Gates a controller action (or a whole controller) on a lifted <c>resource:operation</c> permission KEY:
    /// <c>[RequiresPermissionKey("pipelines:manage")]</c>. The key is decided by the
    /// site's Permission rows <c>(EntityName = resource, EntityId = -1, PermissionName = operation)</c> -
    /// the same rows the mediator's <c>[Requires]</c> consults, so an endpoint and the command behind it
    /// cannot disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is an ordinary <see cref="AuthorizeAttribute"/> carrying a policy NAME, so the whole ASP.NET Core
    /// authorization pipeline (endpoint metadata, <c>[AllowAnonymous]</c>, the authorization middleware)
    /// behaves exactly as it does for every other policy. The name is PREFIXED with <see cref="PolicyPrefix"/>
    /// so the framework's own dynamic <c>EntityName:PermissionName:Roles</c> provider is untouched - and so a
    /// key can never be mistaken for one of those: MEASURED, the un-prefixed key <c>gatetest:manage</c>
    /// declared as a policy would have to be 3-segment to resolve at all, and a 3-segment string such as
    /// <c>key:gatetest:manage</c> resolves through that provider as
    /// <c>PermissionRequirement(EntityName = "key", PermissionName = "gatetest", Roles = "manage")</c> - an
    /// entity with no rows, decided by fallback on a role literally named "manage". The prefix branch is
    /// therefore checked FIRST in <c>AuthorizationPolicyProvider</c>.
    /// </para>
    /// <para>
    /// <b>The trap that buys from being an ordinary AuthorizeAttribute.</b> A class-level
    /// <c>[AllowAnonymous]</c> DISABLES this gate completely - the authorization middleware short-circuits on
    /// that metadata however many policies the endpoint carries - while the mediator's <c>[Requires]</c> on
    /// the command behind the same controller still denies. Driven: the key-gated action returned 200 to an
    /// anonymous caller and the dispatching action beside it returned 403, so testing the dispatching one
    /// "proves" a gate that is not there. <c>PermissionKeyDeclarationGateTests</c> fails the build on it.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class RequiresPermissionKeyAttribute : AuthorizeAttribute
    {
        /// <summary>The policy-name prefix that routes a policy to <see cref="PermissionKeyRequirement"/>.</summary>
        public const string PolicyPrefix = "key:";

        public RequiresPermissionKeyAttribute(string key)
        {
            Key = key;
            Policy = PolicyName(key);
        }

        /// <summary>The <c>resource:operation</c> key this action requires.</summary>
        public string Key { get; }

        /// <summary>The policy name a key resolves through - <c>"key:" + key</c>.</summary>
        public static string PolicyName(string key) => PolicyPrefix + key;

        /// <summary>
        /// True when <paramref name="policyName"/> is a key policy; <paramref name="key"/> is the key it carries.
        /// </summary>
        public static bool TryGetKey(string policyName, out string key)
        {
            key = "";
            if (string.IsNullOrEmpty(policyName) || !policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            key = policyName.Substring(PolicyPrefix.Length);
            return key.Length > 0;
        }
    }
}