using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Services;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Expressions.Features;

/// <summary>
/// Installs and configures the expressions feature.
/// </summary>
public class ExpressionsFeature : FeatureBase
{
    /// <inheritdoc />
    public ExpressionsFeature(IModule module) : base(module)
    {
    }

    /// <inheritdoc />
    public override void Apply()
    {
        Services
            .AddScoped<IExpressionEvaluator, ExpressionEvaluator>()
            .AddSingleton<IWellKnownTypeRegistry, WellKnownTypeRegistry>();
    }
}