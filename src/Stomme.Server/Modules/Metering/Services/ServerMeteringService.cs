using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Metering;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Services;
using Stomme.Shared;

namespace Stomme.Modules.Metering.Services
{
    /// <summary>
    /// The SERVER side of <see cref="IMeteringService"/>: every call is a mediator dispatch, so each
    /// is decided by its request's <c>[Requires]</c>. Registered <c>AddTransient</c> in the module's
    /// <c>ServerStartup</c> so it wins over the client HTTP implementation on the server.
    /// </summary>
    public sealed class ServerMeteringService(IMediator mediator) : IMeteringService, ITransientService
    {
        public async Task<List<MyUsageLine>> GetMyUsageAsync() =>
            (await mediator.Send(new GetMyUsageQuery())).ToList();

        public async Task<List<QuotaView>> GetQuotasAsync() =>
            (await mediator.Send(new ListQuotasQuery())).ToList();

        public async Task<List<UsageRecordView>> GetUsageAsync(string metric = null, int take = 200) =>
            (await mediator.Send(new ListUsageQuery(metric, take))).ToList();

        public Task<AiUsageDashboard> GetAiDashboardAsync(int days = 30) =>
            mediator.Send(new GetAiUsageDashboardQuery(days));

        public async Task<List<AiUsageRecordView>> GetAiCallsAsync(int take = 200) =>
            (await mediator.Send(new ListAiUsageQuery(take))).ToList();

        public Task<Guid> DefineQuotaAsync(QuotaDefinition definition) =>
            mediator.Send(new DefineQuotaCommand(definition.Metric, definition.Period, definition.Limit, definition.WarnAtPercent));

        public Task ResetQuotaAsync(Guid quotaId) =>
            mediator.Send(new ResetQuotaCommand(quotaId));

        public Task GrantOverrideAsync(Guid quotaId, QuotaOverrideRequest request) =>
            mediator.Send(new GrantQuotaOverrideCommand(quotaId, request.Additional, request.ExpiresUtc));

        public Task SetEnforcementAsync(Guid quotaId, bool enforced) =>
            mediator.Send(new SetQuotaEnforcementCommand(quotaId, enforced));

        public Task DeleteQuotaAsync(Guid quotaId) =>
            mediator.Send(new DeleteQuotaCommand(quotaId));
    }
}
