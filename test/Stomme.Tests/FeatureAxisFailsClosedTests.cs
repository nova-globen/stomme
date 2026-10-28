using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CoreFeatureEvaluator = Stomme.FeatureManagement.Application.IFeatureEvaluator;

namespace Stomme.Tests
{
    /// <summary>
    /// The feature axis of the authorization bridge fails CLOSED (D99, F11). The spike's bridge answered
    /// "enabled" whenever Stomme.FeatureManagement's evaluator was absent, which turned every
    /// <c>[Requires(Feature = "...")]</c> into an ungated request on a host without FeatureManagement.
    /// Driven end-to-end: the REAL <see cref="AuthorizationBehavior{TRequest,TResponse}"/>, the REAL
    /// <see cref="PermissionRowAccessGuard"/> and the REAL <see cref="FeatureEvaluatorBridge"/>, composed by
    /// the real <c>AddStommeAuthorization</c>, over a mediator whose only variable is whether the
    /// FeatureManagement evaluator is registered.
    /// </summary>
    public class FeatureAxisFailsClosedTests
    {
        [Fact]
        public async Task A_feature_gated_request_is_denied_when_no_feature_evaluator_is_registered()
        {
            using var provider = Compose(featureEvaluator: null);
            using var scope = provider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var handler = scope.ServiceProvider.GetRequiredService<ProbeHandler>();

            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => mediator.Send(new FeatureGatedProbe()));

            Assert.Contains(nameof(FeatureGatedProbe), denied.Message);
            Assert.False(handler.Ran, "the handler ran for a denied request — the gate is not outermost or not enforced");
        }

        [Fact]
        public async Task A_feature_gated_request_follows_the_evaluator_when_one_is_registered()
        {
            using var on = Compose(new StubCoreFeatureEvaluator(enabled: true));
            using (var scope = on.CreateScope())
            {
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                Assert.Equal("handled", await mediator.Send(new FeatureGatedProbe()));
                Assert.True(scope.ServiceProvider.GetRequiredService<ProbeHandler>().Ran);
            }

            using var off = Compose(new StubCoreFeatureEvaluator(enabled: false));
            using (var scope = off.CreateScope())
            {
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                await Assert.ThrowsAsync<AccessDeniedException>(() => mediator.Send(new FeatureGatedProbe()));
                Assert.False(scope.ServiceProvider.GetRequiredService<ProbeHandler>().Ran);
            }
        }

        // The evaluator is asked for the key the attribute names — not for something the bridge invented.
        [Fact]
        public async Task The_bridge_forwards_the_declared_feature_key()
        {
            var evaluator = new StubCoreFeatureEvaluator(enabled: true);
            using var provider = Compose(evaluator);
            using var scope = provider.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new FeatureGatedProbe());

            Assert.Equal(new[] { "gate-test-feature" }, evaluator.Keys);
        }

        // An un-annotated request is untouched by any of this, evaluator or not.
        [Fact]
        public async Task An_ungated_request_passes_without_an_evaluator()
        {
            using var provider = Compose(featureEvaluator: null);
            using var scope = provider.CreateScope();

            Assert.Equal("handled", await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new UngatedProbe()));
        }

        private static ServiceProvider Compose(CoreFeatureEvaluator? featureEvaluator)
        {
            var services = new ServiceCollection();

            // An authenticated Administrator on site 7: the PERMISSION axis is not what varies here.
            var identity = new ClaimsIdentity(Constants.AuthenticationScheme);
            identity.AddClaim(new Claim(ClaimTypes.Name, "alice"));
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "5"));
            identity.AddClaim(new Claim(ClaimTypes.Role, RoleNames.Admin));
            var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
            context.Items[Constants.HttpContextAliasKey] = new Alias { SiteId = 7, TenantId = 1 };
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
            services.AddSingleton<IUserPermissions>(new NeverAskedUserPermissions());

            if (featureEvaluator != null)
            {
                services.AddSingleton(featureEvaluator);
            }

            // The real composition — the same call the host's composition root makes.
            services.AddStommeAuthorization<PermissionRowAccessGuard, StommeCurrentUser, FeatureEvaluatorBridge>();

            services.AddScoped<ProbeHandler>();
            services.AddScoped<IRequestHandler<FeatureGatedProbe, string>>(sp => sp.GetRequiredService<ProbeHandler>());
            services.AddScoped<IRequestHandler<UngatedProbe, string>>(sp => sp.GetRequiredService<ProbeHandler>());

            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        [Requires(Feature = "gate-test-feature")]
        public sealed class FeatureGatedProbe : IRequest<string>;

        public sealed class UngatedProbe : IRequest<string>;

        public sealed class ProbeHandler : IRequestHandler<FeatureGatedProbe, string>, IRequestHandler<UngatedProbe, string>
        {
            public bool Ran { get; private set; }

            public Task<string> Handle(FeatureGatedProbe request, CancellationToken ct)
            {
                Ran = true;
                return Task.FromResult("handled");
            }

            public Task<string> Handle(UngatedProbe request, CancellationToken ct)
            {
                Ran = true;
                return Task.FromResult("handled");
            }
        }

        private sealed class StubCoreFeatureEvaluator : CoreFeatureEvaluator
        {
            private readonly bool _enabled;

            public StubCoreFeatureEvaluator(bool enabled)
            {
                _enabled = enabled;
            }

            public List<string> Keys { get; } = new();

            public Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
            {
                Keys.Add(key);
                return Task.FromResult(_enabled);
            }

            public Task<bool> IsEnabledAsync(string key, FeatureContext context, CancellationToken ct = default)
                => IsEnabledAsync(key, ct);
        }

        // No probe here carries a permission, so the rows are never consulted; any call is a defect.
        private sealed class NeverAskedUserPermissions : IUserPermissions
        {
            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, int siteId, string entityName, int entityId, string permissionName, string roles) => throw new InvalidOperationException("rows consulted for a feature-only gate");

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, int siteId, string entityName, int entityId, string permissionName) => throw new InvalidOperationException("rows consulted for a feature-only gate");

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string permissionName, List<Permission> permissions) => throw new NotSupportedException();

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string permissionName, string permissions) => throw new NotSupportedException();

            public Task<User> GetUserAsync(ClaimsPrincipal user) => throw new NotSupportedException();

            public Task<User> GetUserAsync() => throw new NotSupportedException();

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string entityName, int entityId, string permissionName) => throw new NotSupportedException();
        }
    }
}
