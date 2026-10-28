using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Tests.Fixtures.GateTestModule
{
    /// <summary>
    /// The SERVER half of the BL-99 gate fixture: the handler behind the <c>[Requires]</c> command, the
    /// startup that registers it, and a controller carrying BOTH gates a lifted module can use - the new
    /// <see cref="RequiresPermissionKeyAttribute"/> (D99 point 9) and, as a KNOWN-DIRTY control, the
    /// framework's dynamic 3-segment policy the survey refuted as a carrier for lifted keys (F8).
    /// </summary>
    /// <remarks>
    /// Discovered by the SAME startup scan a real module rides: the assembly is named "Stomme.Tests", so
    /// <c>IsStommeAssembly</c> matches it, <c>AddStommeServices</c> runs the <see cref="ServerStartup"/>
    /// below, and <c>AddStommeApplicationParts</c> registers <see cref="GateTestController"/> as an MVC
    /// application part. Nothing here is wired by hand in a test container - that is the point: the gates
    /// drive the composed production host.
    /// </remarks>
    public sealed class GateTestManageHandler : IRequestHandler<GateTestManageCommand, string>
    {
        /// <summary>What the handler returns - the marker proving it RAN rather than merely being reachable.</summary>
        public const string Marker = "gatetest-handler-ran";

        private static int _invocations;

        /// <summary>
        /// How many times the handler has run in this process. Asserted as a DELTA around a request, never as
        /// an absolute: the fixture is shared by every gate suite and the suites run in one process.
        /// </summary>
        public static int Invocations => Volatile.Read(ref _invocations);

        public Task<string> Handle(GateTestManageCommand request, CancellationToken ct)
        {
            Interlocked.Increment(ref _invocations);
            return Task.FromResult(Marker);
        }
    }

    /// <summary>Registers the fixture's mediator handler, the way a lifted module's server startup does.</summary>
    /// <remarks>
    /// It registers the handler EXPLICITLY rather than calling <c>AddMediator(assembly)</c>: that call is
    /// additive, not idempotent (F12), and over this assembly it would also re-register every behavior and
    /// handler the other suites declare. The authorization behavior itself is composed by the HOST, before
    /// <c>AddStommeAssemblies()</c> - a module never registers it.
    /// </remarks>
    public sealed class ServerStartup : IServerStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped<IRequestHandler<GateTestManageCommand, string>, GateTestManageHandler>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }
    }

    /// <summary>
    /// The fixture's Web API surface - <c>api/GateTest</c>. Three actions, one per enforcement path:
    /// <list type="bullet">
    /// <item><c>manage</c> - gated by <see cref="RequiresPermissionKeyAttribute"/>: decided ONLY by the
    /// <c>(gatetest, -1, manage)</c> rows, with no query-string re-scoping and no roles fallback;</item>
    /// <item><c>dispatch</c> - UNGATED at the endpoint, dispatching the <c>[Requires]</c>-carrying command
    /// so the mediator's <c>AuthorizationBehavior</c> is the only thing standing between the caller and the
    /// handler (a denial must surface as 403 ProblemDetails, not 500 - F5);</item>
    /// <item><c>dynamic</c> - the KNOWN-DIRTY control: the framework's own 3-segment policy over the same
    /// resource and operation. F8 says that path lets the CALLER re-scope the row lookup with
    /// <c>?authgatetestid=</c>; the gate drives exactly that and shows the two paths disagreeing.</item>
    /// </list>
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class GateTestController : Controller
    {
        /// <summary>Returned by the key-gated action - a marker a 403 body can never contain.</summary>
        public const string KeyGatedMarker = "gatetest-key-ok";

        /// <summary>Returned by the dynamic-policy action.</summary>
        public const string DynamicGatedMarker = "gatetest-dynamic-ok";

        private readonly IMediator _mediator;

        public GateTestController(IMediator mediator)
        {
            _mediator = mediator;
        }

        [HttpGet("manage")]
        [RequiresPermissionKey(GateTestPermissions.Manage)]
        public string Manage() => KeyGatedMarker;

        [HttpGet("dispatch")]
        public async Task<string> Dispatch() => await _mediator.Send(new GateTestManageCommand());

        [HttpGet("dynamic")]
        [Authorize(Policy = GateTestPermissions.Manage + ":" + RoleNames.Admin)]
        public string Dynamic() => DynamicGatedMarker;
    }
}