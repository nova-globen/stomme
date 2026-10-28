using Stomme.Engine.Common.Features;
using Stomme.Engine.Extensions;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Attributes;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management.Features;
using Stomme.Engine.Pipelines.Runtime.Features;

namespace Stomme.Engine.Features;

/// <summary>
/// Represents Stomme as a feature of the system.
/// </summary>
[DependsOn(typeof(MediatorFeature))]
[DependsOn(typeof(PipelinesFeature))]
[DependsOn(typeof(FlowchartFeature))]
[DependsOn(typeof(DefaultPipelineRuntimeFeature))]
[DependsOn(typeof(PipelineManagementFeature))]
public class EngineFeature : FeatureBase
{
    /// <summary>
    /// Set this to true to opt out of automatically registering steps from Stomme.Engine.Pipelines.
    /// </summary>
    public bool DisableAutomaticStepRegistration { get; set; }

    /// <summary>
    /// More of the step execution pipeline, applied after the default chain is built (Stomme BL-229 increment 9: the
    /// Pipelines module inserts its per-step retry and continue-on-error just inside the exception handling). Set it in the
    /// <c>AddEngine</c> callback; it is read when the step pipeline is first built.
    /// </summary>
    public Action<Stomme.Engine.Pipelines.IStepExecutionPipelineBuilder>? ConfigureStepExecutionPipeline { get; set; }

    /// <inheritdoc />
    public EngineFeature(IModule module) : base(module)
    {
    }

    /// <inheritdoc />
    public override void Configure()
    {
        Module
            .UsePipelines(pipelines => pipelines
                .WithDefaultPipelineExecutionPipeline()
                // Read when the pipeline is built, not now: the AddEngine callback that sets it may run after this.
                .WithDefaultStepExecutionPipeline(builder => ConfigureStepExecutionPipeline?.Invoke(builder)))
            .UsePipelineManagement(management =>
            {
                if (!DisableAutomaticStepRegistration)
                    management
                        .AddStepsFrom<WriteLine>()
                        .RemoveStep<ReadLine>() // ReadLine is not commonly used and can cause "hanging" containers when awaiting user input. Better to opt-in explicitly. 
                        ;
            });
    }
}