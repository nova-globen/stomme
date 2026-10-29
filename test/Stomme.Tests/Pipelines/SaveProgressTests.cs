#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.CommitStates.Tasks;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's "save execution progress", run by the real engine with the module's own registration: a pipeline whose
    /// definition names the save-progress strategy is stored after each step - a later step finds the earlier one's record already
    /// stored - and one that does not is stored only when its burst ends; the setting reaches the definition's options at a save.
    /// </summary>
    public sealed class SaveProgressTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<Sink>();
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<CountStored>();
                PipelineRunPolicy.UseSaveProgress(module); // as the host registers it
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();

            // The engine's commit strategies reach its registry through a startup task, which a host runs when its tenant activates
            // (TenantTaskManager) - run here as it does there, or the strategy a definition names is found nowhere.
            foreach (var task in scope.ServiceProvider.GetServices<IStartupTask>().OfType<PopulateCommitStrategyRegistry>())
            {
                await task.ExecuteAsync(default);
            }
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        public sealed class Sink
        {
            public List<long> Stored { get; } = new();
        }

        [Step("Stomme.Tests", "Tests", "Counts the step records stored so far for its run (save progress).")]
        public sealed class CountStored : Step
        {
            protected override async ValueTask ExecuteAsync(StepExecutionContext context)
            {
                var stored = await context.GetRequiredService<IStepExecutionStore>().CountAsync(new StepExecutionRecordFilter { PipelineInstanceId = context.PipelineExecutionContext.Id });
                context.GetRequiredService<Sink>().Stored.Add(stored);
                await context.CompleteStepAsync();
            }
        }

        // A run of two steps - a log line, then the count - with the setting the save writes onto a definition.
        private async Task<long> StoredWhenTheSecondStepRunsAsync(bool? saveProgress)
        {
            var sink = _services.GetRequiredService<Sink>();
            sink.Stored.Clear();
            var definition = new PipelineDefinition();
            PipelineRunPolicy.Apply(definition, null, saveProgress);
            var pipeline = new Pipeline(new Sequence { Steps = { new LogMessage { Message = new("first") }, new CountStored() } }) { Options = definition.Options };

            using var scope = _services.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(pipeline);
            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            return Assert.Single(sink.Stored);
        }

        [Fact]
        public async Task A_pipeline_that_saves_its_progress_is_stored_after_each_step_and_one_that_does_not_when_it_ends()
        {
            Assert.True(await StoredWhenTheSecondStepRunsAsync(saveProgress: true) > 0, "the first step's record was not stored before the second ran");
            Assert.Equal(0, await StoredWhenTheSecondStepRunsAsync(saveProgress: null));
        }

        [Fact]
        public void The_setting_names_the_strategy_on_the_definitions_options_and_clears_it_when_off()
        {
            var definition = new PipelineDefinition();
            PipelineRunPolicy.Apply(definition, "one", true);
            Assert.Equal((PipelineRunPolicy.SaveProgressStrategy, typeof(Stomme.Engine.Pipelines.Runtime.ActivationValidators.SingletonStrategy)), (definition.Options.CommitStrategyName, definition.Options.ActivationStrategyType));
            PipelineRunPolicy.Apply(definition, "one", null);
            Assert.Null(definition.Options.CommitStrategyName);
        }
    }
}
