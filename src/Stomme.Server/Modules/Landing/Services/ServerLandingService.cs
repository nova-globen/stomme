#nullable enable
using System.Threading.Tasks;
using Stomme.Landing;
using Stomme.Mediator;
using Stomme.Modules.Landing.Application;

namespace Stomme.Modules.Landing.Services
{
    /// <summary>The landing service on the SERVER (Blazor Server, prerender): the API's requests, sent directly.</summary>
    public sealed class ServerLandingService(IMediator mediator) : ILandingService
    {
        public Task<LandingPageView?> GetAsync() => mediator.Send(new GetLandingPageQuery());

        public Task<PredefinedLandingContent> GetTemplateAsync() => mediator.Send(new GetLandingTemplateQuery());

        public Task<LandingPageView> ConfigurePredefinedAsync(ConfigurePredefinedLandingRequest request) =>
            mediator.Send(new ConfigurePredefinedLandingCommand(new PredefinedLandingContent(request.Headline, request.Introduction, request.Sections)));

        public Task<LandingPageView> ConfigureDashboardAsync(ConfigureDashboardLandingRequest request) =>
            mediator.Send(new ConfigureDashboardLandingCommand(request.DashboardId));

        public Task<LandingPageView> PublishAsync() => mediator.Send(new PublishLandingCommand());

        public Task<LandingPageView> UnpublishAsync() => mediator.Send(new UnpublishLandingCommand());

        public Task ResetAsync() => mediator.Send(new ResetLandingCommand());

        public Task<LandingDraftView> GenerateAsync(GenerateLandingRequest request) => mediator.Send(new GenerateLandingDraftCommand(request.Brief));

        public Task<LandingDraftView> ProposeSectionAsync(ProposeLandingSectionRequest request) => mediator.Send(new ProposeLandingSectionCommand(request.Section, request.Instruction));

        public Task<LandingPageView> ConfigureGeneratedAsync(ConfigureGeneratedLandingRequest request) => mediator.Send(new ConfigureGeneratedLandingCommand(request.Html));
    }
}
