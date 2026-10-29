using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Tests;

/// <summary>
/// The CONTACT SURFACE. This is the whole of what a Stomme module (the Pipelines module) ever asks of this engine: compose it with
/// <see cref="ModuleExtensions.AddEngine"/>, resolve a SCOPED <see cref="IPipelineRunner"/>, hand it one
/// step, and read what the step produced. Everything else in the eleven vendored projects exists to make
/// that work.
///
/// It is a smoke test on purpose - it asserts the engine RUNS, not how. If the deep rename broke the
/// feature graph (AddEngine composes nothing), the DI wiring (no IPipelineRunner), the step model
/// (ExecuteAsync never called), or the scheduler (the pipeline never reaches Finished), this goes red.
/// </summary>
public class ContactSurfaceSmokeTests
{
    private const string Written = "stomme-engine-contact-surface";

    [Fact]
    public async Task One_CodeStep_runs_through_the_pipeline_runner_and_writes_its_result_to_the_sink()
    {
        // 1. Registration: one call composes the engine's feature graph into a plain IServiceCollection.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ResultSink>();
        services.AddEngine();

        await using var provider = services.BuildServiceProvider();

        // 2. The runner is SCOPED - a module resolves it from a request/unit-of-work scope, never the root.
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();

        var sink = provider.GetRequiredService<ResultSink>();
        Assert.Null(sink.Value);

        // 3. Run ONE step. Pipeline.FromStep wraps it; the runner builds the graph and schedules it.
        var result = await runner.RunAsync(new SinkStep(Written));

        // 4. The step's own ExecuteAsync ran, resolved a service from the execution context, and wrote.
        Assert.Equal(Written, sink.Value);

        // And the run itself completed rather than suspending or faulting.
        Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
        Assert.Equal(PipelineSubStatus.Finished, result.PipelineState.SubStatus);
    }

    [Fact]
    public async Task AddEngine_registers_the_runner_as_scoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEngine();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IPipelineRunner));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        // A scoped service cannot be resolved from the root provider when scope validation is on - which
        // is what a Stomme host does. Proving it here is what makes CreateScope() above load-bearing.
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPipelineRunner>());
    }
}

/// <summary>The sink the step writes into - a stand-in for whatever a real module hands the engine.</summary>
public class ResultSink
{
    public string? Value { get; set; }
}

/// <summary>
/// The module-authored step: a <see cref="CodeStep"/> whose ExecuteAsync pulls a service off the execution
/// context and writes to it. This is the shape every predecessor-platform action had.
/// </summary>
[Step("Stomme", "Test", "Writes its text into the test sink.")]
public class SinkStep(string text) : CodeStep
{
    /// <inheritdoc />
    protected override ValueTask ExecuteAsync(StepExecutionContext context)
    {
        context.GetRequiredService<ResultSink>().Value = text;
        return default;
    }
}
