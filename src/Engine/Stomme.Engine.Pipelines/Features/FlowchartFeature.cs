using Stomme.Engine.Extensions;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Options;
using Stomme.Engine.Pipelines.Steps.Flowchart.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Features;

/// <summary>
/// Adds support for the Flowchart step.
/// </summary>
public class FlowchartFeature : FeatureBase
{
    /// <inheritdoc />
    public FlowchartFeature(IModule module) : base(module)
    {
    }

    /// <summary>
    /// A delegate to configure <see cref="FlowchartOptions"/>.
    /// </summary>
    public Action<FlowchartOptions>? FlowchartOptionsConfigurator { get; set; }

    /// <inheritdoc />
    public override void Apply()
    {
        Services.AddSerializationOptionsConfigurator<FlowchartSerializationOptionConfigurator>();

        // Register FlowchartOptions
        Services.AddOptions<FlowchartOptions>();

        if (FlowchartOptionsConfigurator != null)
            Services.Configure(FlowchartOptionsConfigurator);
    }

    public override void Configure()
    {
        Module.AddTypeAlias<FlowScope>("FlowScope");
    }
}