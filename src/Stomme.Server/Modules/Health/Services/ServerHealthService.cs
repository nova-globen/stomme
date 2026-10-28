#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Health;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Services;

namespace Stomme.Modules.Health.Services
{
    /// <summary>
    /// The SERVER side of <see cref="IHealthService"/>: a mediator dispatch, so a control rendered on the server
    /// goes through exactly the same <c>[Requires]</c> decision an HTTP caller does.
    /// </summary>
    /// <remarks>
    /// Registered with an explicit <c>AddTransient</c> so it wins over the client HTTP implementation on the
    /// server — the Guestbook / HtmlText pattern. Without it a server-rendered control would issue an HTTP request
    /// back to its own host, which works right up until the loopback is not reachable.
    /// </remarks>
    public sealed class ServerHealthService : IHealthService
    {
        private readonly IMediator _mediator;

        public ServerHealthService(IMediator mediator) => _mediator = mediator;

        public Task<HealthDashboard> GetDashboardAsync() =>
            _mediator.Send(new GetHealthDashboardQuery());

        public async Task<List<HealthRollupView>> GetRollupsAsync(string window = "Hour", int take = 200) =>
            (await _mediator.Send(new ListHealthRollupsQuery(window, take))).ToList();

        public async Task AcknowledgeAsync(Guid alertId) =>
            await _mediator.Send(new AcknowledgeHealthAlertCommand(alertId));

        public async Task<HealthRunNowResult> RunNowAsync()
        {
            try
            {
                return await _mediator.Send(new RunHealthProbesNowCommand());
            }
            catch (InvalidOperationException ex) when (ex.Message == RunHealthProbesNowCommandHandler.AlreadyRunning)
            {
                return null!;   // the HTTP client's answer to the controller's 409
            }
        }
    }
}
