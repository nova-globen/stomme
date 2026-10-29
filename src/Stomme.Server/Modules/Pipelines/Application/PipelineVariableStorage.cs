#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// What a run saves is kept with the run. The engine holds a variable that names no storage in memory only, so
    /// whatever a run had saved was gone once it paused - after a Wait, an approval, a call or a form waited for (measured
    /// 2026-10-07: a value saved before a two-hour Wait read empty after it) - and a retry from a failed step had nothing of
    /// the steps before it. The engine's own designer names the run's storage for each variable it writes; this editor's codec
    /// now does (<see cref="DriverTypeName"/>), and a body saved before it is given the same when its graph is built, so a
    /// pipeline published earlier keeps what it saves too without being saved again.
    /// </summary>
    public sealed class PipelineVariableStorage(IPipelineGraphBuilder inner) : IPipelineGraphBuilder
    {
        /// <summary>The storage a pipeline's variables name: the run's own state, which the engine stores with the run.</summary>
        public static readonly string DriverTypeName = typeof(PipelineInstanceStorageDriver).GetSimpleAssemblyQualifiedName();

        public Task<PipelineGraph> BuildAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
        {
            Keep(pipeline);
            return inner.BuildAsync(pipeline, cancellationToken);
        }

        /// <summary>The variables an author declares - the pipeline's and its flowchart's - kept with the run when they name no storage.</summary>
        public static void Keep(Pipeline pipeline)
        {
            var declared = pipeline.Variables.Concat((pipeline.Root as IVariableContainer)?.Variables ?? Enumerable.Empty<Stomme.Engine.Pipelines.Memory.Variable>());
            foreach (var variable in declared.Where(variable => variable.StorageDriverType is null))
            {
                variable.StorageDriverType = typeof(PipelineInstanceStorageDriver);
            }
        }

        /// <summary>Puts the keeping in front of the engine's graph builder - composed after the engine, as the host does.</summary>
        public static void Wrap(IServiceCollection services)
        {
            var engine = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IPipelineGraphBuilder))
                ?? throw new InvalidOperationException("The engine registered no graph builder to keep a pipeline's variables in.");
            services.AddScoped<IPipelineGraphBuilder>(provider => new PipelineVariableStorage(engine switch
            {
                { ImplementationFactory: { } factory } => (IPipelineGraphBuilder)factory(provider),
                { ImplementationInstance: IPipelineGraphBuilder instance } => instance,
                { ImplementationType: { } type } => (IPipelineGraphBuilder)ActivatorUtilities.CreateInstance(provider, type),
                _ => throw new InvalidOperationException("The engine's graph builder registration has no implementation."),
            }));
        }
    }
}
