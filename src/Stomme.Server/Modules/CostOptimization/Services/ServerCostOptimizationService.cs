#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Mediator;
using Stomme.Modules.CostOptimization.Application;
using Stomme.Shared;

namespace Stomme.Modules.CostOptimization.Services
{
    /// <summary>The page's service on the server: the same commands and queries the API sends, through the mediator (the Guestbook pattern).</summary>
    public sealed class ServerCostOptimizationService(IMediator mediator) : ICostOptimizationService, ITransientService
    {
        public async Task<List<RecommendationView>> ListAsync() => (await mediator.Send(new ListRecommendationsQuery())).ToList();

        public Task<AdvisorRunView> RunAdvisorAsync() => mediator.Send(new RaiseRecommendationsCommand());

        public Task<RecommendationDecisionView> ApplyAsync(Guid recommendationId, ApplyRecommendationRequest request) =>
            mediator.Send(new ApplyRecommendationCommand(recommendationId, request.ActionTaken));

        public Task<RecommendationDecisionView> DismissAsync(Guid recommendationId, DismissRecommendationRequest request) =>
            mediator.Send(new DismissRecommendationCommand(recommendationId, request.Reason, request.CooldownDays));
    }
}
