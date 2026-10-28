#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// A step test stores nothing (BL-229 increment 6's promise, kept since 2026-10-06): its run is the engine's like any other,
    /// so the engine's commit handler saved it - every step test left a run of definition "1" in the site's All runs, with its log
    /// (measured on a scratch host: five of them). This wraps the engine's handler and commits nothing for a run marked as a step
    /// test (<see cref="PropertyKey"/> on the run's properties, set by <see cref="TestStepCommandHandler"/>); every other run - a
    /// pipeline a tested step dispatches included, which is a run of its own - commits as before.
    /// </summary>
    public sealed class StepTestCommitGate(ICommitStateHandler inner) : ICommitStateHandler
    {
        public const string PropertyKey = "stomme:stepTest";

        public static bool IsStepTest(PipelineExecutionContext context) =>
            context.Properties is { } properties && properties.TryGetValue(PropertyKey, out var value) && value is true;

        public Task CommitAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default) =>
            IsStepTest(pipelineExecutionContext) ? Task.CompletedTask : inner.CommitAsync(pipelineExecutionContext, cancellationToken);

        public Task CommitAsync(PipelineExecutionContext pipelineExecutionContext, PipelineState pipelineState, CancellationToken cancellationToken = default) =>
            IsStepTest(pipelineExecutionContext) ? Task.CompletedTask : inner.CommitAsync(pipelineExecutionContext, pipelineState, cancellationToken);

        /// <summary>Wraps the commit handler the engine registered (called after <c>AddEngine</c>); the last registration is the one resolved.</summary>
        public static void Wrap(IServiceCollection services)
        {
            var engine = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(ICommitStateHandler))
                ?? throw new InvalidOperationException("The engine registered no commit handler for the step test to wrap.");
            services.AddScoped<ICommitStateHandler>(provider => new StepTestCommitGate(engine switch
            {
                { ImplementationFactory: { } factory } => (ICommitStateHandler)factory(provider),
                { ImplementationInstance: ICommitStateHandler instance } => instance,
                { ImplementationType: { } type } => (ICommitStateHandler)ActivatorUtilities.CreateInstance(provider, type),
                _ => throw new InvalidOperationException("The engine's commit handler registration has no implementation."),
            }));
        }
    }
}
