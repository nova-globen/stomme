using Stomme.Engine.Mediator.Middleware.Command;
using Stomme.Engine.Mediator.Middleware.Command.Contracts;
using Stomme.Engine.Tenants.Mediator.Middleware;
using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;

namespace Stomme.Engine.Tenants.Mediator.Tasks;

[UsedImplicitly]
public class SetupMediatorPipelines(ICommandPipeline commandPipeline) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        commandPipeline.Setup(executionPipeline => executionPipeline.UseMiddleware<TenantPropagatingMiddleware>(0));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}