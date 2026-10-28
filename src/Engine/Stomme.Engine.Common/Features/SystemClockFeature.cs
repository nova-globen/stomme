using Stomme.Engine.Common.Services;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Common.Features;

/// <summary>
/// Configures the system clock.
/// </summary>
public class SystemClockFeature : FeatureBase
{
    /// <inheritdoc />
    public SystemClockFeature(IModule module) : base(module)
    {
    }

    /// <inheritdoc />
    public override void Apply()
    {
        Services.AddSingleton<ISystemClock, SystemClock>();
    }
}