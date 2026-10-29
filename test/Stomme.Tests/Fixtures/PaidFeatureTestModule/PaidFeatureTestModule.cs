using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Tests.Fixtures.PaidFeatureTestModule
{
    /// <summary>The paid-feature fixture's vocabulary: one feature key, gated by one canonical entitlement.</summary>
    public static class PaidFeatureTestFeatures
    {
        public const string Key = "paidtest.pro";
        public const string Entitlement = "stomme.tests.paidtest.pro";
    }

    /// <summary>
    /// Declares the paid feature the way a module does: enabled by default, gated by a required
    /// entitlement. With no <c>IEntitlementProvider</c> registered (mode unset) the entitlement leg is permissive, so the
    /// feature is simply on; under <c>Deployment:Mode=OnPrem</c> it is on only while a Valid licence key grants
    /// <see cref="PaidFeatureTestFeatures.Entitlement"/>.
    /// </summary>
    /// <remarks>
    /// Its constructor is deliberately NOT public, so <c>AddDiscoveredFeatureContributors</c> (which takes only types with a
    /// public parameterless constructor from every loaded Stomme* assembly) does not pick it up. It is registered by
    /// <see cref="ServerStartup"/> instead - the explicit path <c>IFeatureContributor</c> documents - so it reaches exactly
    /// the hosts that run the module scan. MEASURED: as a discoverable contributor it was seeded by every bare
    /// <c>AddFeatureManagement</c> composition in this assembly too, and <c>PerTenantFeatureFlagTests</c> (which pins the
    /// stock catalog's 2 rows per tenant) went red, Expected 2 / Actual 3, in two facts.
    /// </remarks>
    public sealed class PaidFeatureTestContributor : IFeatureContributor
    {
        internal PaidFeatureTestContributor()
        {
        }
        public IReadOnlyCollection<FeatureDescriptor> Features { get; } = new[]
        {
            new FeatureDescriptor(
                PaidFeatureTestFeatures.Key,
                "BL-2 fixture: a paid feature enforced through the mediator",
                Category: "Tests",
                DefaultEnabled: true,
                RequiredEntitlement: PaidFeatureTestFeatures.Entitlement)
        };
    }

    /// <summary>
    /// The paid capability's request. The <c>[Requires(Feature)]</c> sits on the REQUEST TYPE because that is the only
    /// place it is enforced: <c>AuthorizationBehavior</c> evaluates <c>request.GetType()</c>, and no MVC filter reads the
    /// attribute at all (pinned by <c>PermissionKeyDeclarationGateTests</c>).
    /// </summary>
    [Requires(Feature = PaidFeatureTestFeatures.Key)]
    public sealed record PaidFeatureTestCommand : IRequest<string>;

    /// <summary>The paid capability itself - returns a marker and counts its invocations.</summary>
    public sealed class PaidFeatureTestHandler : IRequestHandler<PaidFeatureTestCommand, string>
    {
        /// <summary>Returned when the handler RAN - a marker no 403 body can contain.</summary>
        public const string Marker = "paidtest-handler-ran";

        private static int _invocations;

        /// <summary>Process-wide invocation count. Assert it as a DELTA around a request, never as an absolute.</summary>
        public static int Invocations => Volatile.Read(ref _invocations);

        public Task<string> Handle(PaidFeatureTestCommand request, CancellationToken ct)
        {
            Interlocked.Increment(ref _invocations);
            return Task.FromResult(Marker);
        }
    }

    /// <summary>
    /// Registers the handler EXPLICITLY - never <c>AddMediator(assembly)</c>, which is additive (F12) - and the feature
    /// contributor (see <see cref="PaidFeatureTestContributor"/> for why it is not discovered).
    /// </summary>
    public sealed class ServerStartup : IServerStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped<IRequestHandler<PaidFeatureTestCommand, string>, PaidFeatureTestHandler>();
            services.AddSingleton<IFeatureContributor>(new PaidFeatureTestContributor());
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }
    }

    /// <summary>
    /// <c>api/PaidFeatureTest</c>. Deliberately carries NO <c>[Requires]</c> and NO <c>[Authorize]</c>: the controller only
    /// DISPATCHES, and the mediator is the whole paywall. A denial surfaces as 403 ProblemDetails.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class PaidFeatureTestController : Controller
    {
        private readonly IMediator _mediator;

        public PaidFeatureTestController(IMediator mediator)
        {
            _mediator = mediator;
        }

        [HttpGet("run")]
        public async Task<string> Run() => await _mediator.Send(new PaidFeatureTestCommand());
    }
}