using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Common.Features;

/// <summary>
/// Adds and configures the Mediator feature.
/// </summary>
public class MediatorFeature : FeatureBase
{
    /// <inheritdoc />
    public MediatorFeature(IModule module) : base(module)
    {
    }

    /// <inheritdoc />
    public override void Apply()
    {
        Services
            .AddMediator()
            .AddMediatorHostedServices();
    }
}