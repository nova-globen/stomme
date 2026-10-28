#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Modules.Pipelines.Application;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The editor's palette offers the steps the canvas can author, and names a step's BODIES apart from its outcomes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2026-10-02 (found walking the editor in a browser): a step with an embedded port - a loop's Body, an If's Then and Else -
    /// had its child slots drawn as outcome ports and could only be saved empty, so such steps were left out.
    /// </para>
    /// <para>
    /// 2026-10-05 (BL-95): the editor opens each embedded port as a flowchart of its own, so those steps are offered, the
    /// embedded ports listed as <c>Bodies</c> and NEVER as <c>Ports</c> (the outcomes a connection leaves from). Still out:
    /// a container whose children are a list (Sequence, Parallel - the canvas is that list) and Switch, whose cases each
    /// hold a step no body names. Read over the engine as the host composes it, so a step the engine gains is judged by the
    /// same rule without a list to keep.
    /// </para>
    /// </remarks>
    public sealed class PipelineStepCatalogTests
    {
        [Fact]
        public async Task A_steps_bodies_are_offered_as_bodies_never_as_outcomes_and_list_containers_stay_out()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEngine();
            await using var engine = services.BuildServiceProvider();
            using var scope = engine.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var populator = scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>();
            await populator.PopulateAsync();

            var catalog = await new ListStepsQueryHandler(registry, populator).Handle(new ListStepsQuery(), CancellationToken.None);
            var entry = catalog.ToDictionary(item => item.Type);

            // The loops and If hold steps in their bodies, and have no outcome of their own beyond the one implicit "done".
            Assert.Equal(new[] { "Body" }, entry["Stomme.ForEach"].Bodies);
            Assert.Equal(new[] { "Body" }, entry["Stomme.While"].Bodies);
            Assert.Equal(new[] { "Body" }, entry["Stomme.For"].Bodies);
            Assert.Equal(new[] { "Then", "Else" }, entry["Stomme.If"].Bodies);
            Assert.All(new[] { "Stomme.ForEach", "Stomme.While", "Stomme.For", "Stomme.If" }, type => Assert.Empty(entry[type].Ports));

            // No step offers a body as an outcome: every outcome the catalog names is a FLOW port of the engine's.
            foreach (var item in catalog)
            {
                var descriptor = registry.Find(item.Type, item.Version)!;
                Assert.All(item.Ports, port => Assert.Equal(PortType.Flow, descriptor.Ports.Single(candidate => candidate.Name == port).Type));
                Assert.All(item.Bodies, body => Assert.Equal(PortType.Embedded, descriptor.Ports.Single(candidate => candidate.Name == body).Type));
            }

            // Not vacuous, and still out: the engine HAS list containers (the flowchart, at least) and a Switch, and none is offered.
            var lists = registry.ListAll().Where(descriptor => typeof(Container).IsAssignableFrom(descriptor.ClrType)).Select(descriptor => descriptor.TypeName).ToList();
            Assert.Contains("Stomme.Flowchart", lists);
            Assert.Empty(catalog.Select(item => item.Type).Intersect(lists));
            Assert.Contains(registry.ListAll(), descriptor => descriptor.TypeName == "Stomme.Switch" && descriptor.IsBrowsable);
            Assert.DoesNotContain(catalog, item => item.Type == "Stomme.Switch");

            // The flowchart's own branching stays.
            Assert.Contains(catalog, item => item.Type == "Stomme.FlowDecision" && item.Ports.SequenceEqual(new[] { "True", "False" }) && item.Bodies.Count == 0);
            Assert.Contains(catalog, item => item.Type == "Stomme.WriteLine");
            Assert.Contains(catalog, item => item.Type == "Stomme.FlowJoin");
        }

        // BL-229 increment 4: an input is REQUIRED when the step leaves it null (no starting value) and its type holds no null -
        // read from the steps as the host composes them, the module's own families included.
        [Fact]
        public async Task An_input_the_step_leaves_without_a_value_is_required_and_one_with_a_default_or_a_nullable_type_is_not()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEngine(module => module.AddStepsFrom<Stomme.Modules.Pipelines.Steps.PipelineStepsMarker>());
            await using var engine = services.BuildServiceProvider();
            using var scope = engine.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var populator = scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>();
            await populator.PopulateAsync();

            var catalog = await new ListStepsQueryHandler(registry, populator).Handle(new ListStepsQuery(), CancellationToken.None);
            bool Required(string type, string input) => catalog.Single(item => item.Type == type).Inputs.Single(candidate => candidate.Name == input).IsRequired;

            // Left null (= null!) with a type that holds no null: required.
            Assert.True(Required("Stomme.WriteLine", "Text"));
            Assert.True(Required("Stomme.SendHttpRequest", "Url"));
            Assert.True(Required("Stomme.LogMessage", "Message"));
            // A starting value: not required, whatever its type.
            Assert.False(Required("Stomme.SendHttpRequest", "Method"));
            Assert.False(Required("Stomme.Delay", "Seconds"));
            Assert.False(Required("Stomme.ParseCsv", "Delimiter"));
            // Left null, but its type is nullable - the step reads it with a fallback: not required.
            Assert.False(Required("Stomme.Fault", "Message"));
            Assert.False(Required("Stomme.Fault", "Code"));
            Assert.False(Required("Stomme.FlowFork", "Branches"));
            // A value type left unset runs as its default - an untouched checkbox is false: never required.
            Assert.False(Required("Stomme.DispatchPipeline", "WaitForCompletion"));
            var values = new[] { "Boolean", "Int16", "Int32", "Int64", "Single", "Double", "Decimal" };
            Assert.DoesNotContain(catalog.SelectMany(item => item.Inputs), input => input.IsRequired && (values.Contains(input.TypeName) || input.Options is { Count: > 0 }));
        }
    }
}
