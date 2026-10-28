#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - Wait for a form (n8n's Wait node resumed "on form submitted"): the run parks on a bookmark keyed by its call
    /// address's token hash, carrying the form; the form address shows that form; a submission resumes the run down Submitted with
    /// the answers as the result - once, the form closed after; nobody filling it in time goes down Timed out; a wait with no call
    /// address before it, or with fields that are no form, fails saying why.
    /// </summary>
    public sealed class WaitForFormTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));

        private static readonly string Fields = PipelineForms.Serialize(new[]
        {
            new PipelineFormField { Label = "Your name", Type = PipelineFormFieldTypes.Text, Required = true, Name = "name" },
            new PipelineFormField { Label = "E-mail", Type = PipelineFormFieldTypes.Email, Name = "email" },
        });

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(new PipelineTriggerOptions()));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton<ISystemClock>(_clock);
            services.AddScoped<DueWaitsTask>();
            services.AddScoped<PipelineFormWaits>();
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<IStimulusDispatcher, InlineStimulusDispatcher>(); // the host's dispatcher works in the background
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private sealed class FakeClock(DateTimeOffset now) : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = now;
        }

        private sealed class InlineStimulusDispatcher(IStimulusSender sender) : IStimulusDispatcher
        {
            public async Task<DispatchStimulusResponse> SendAsync(DispatchStimulusRequest request, CancellationToken cancellationToken = default)
            {
                await sender.SendAsync(request.StepTypeName!, request.Stimulus!, request.Metadata, cancellationToken);
                return DispatchStimulusResponse.Empty;
            }
        }

        // A webhook starts it; Call address (unless left out); Wait for a form; each outcome writes a line of its own.
        private static async Task<string> PublishAsync(IServiceProvider services, string hook, WaitForForm wait, bool address = true)
        {
            var trigger = new Webhook { WebhookName = new(hook), Id = "trigger" };
            trigger.SetCanStartPipeline(true);
            wait.Id = "wait-form";
            var submitted = new WriteLine("submitted " + hook) { Id = "submitted" };
            var timedOut = new WriteLine("timed out " + hook) { Id = "timed-out" };
            var flowchart = new Flowchart { Id = "flow", Steps = { trigger, wait, submitted, timedOut } };
            if (address)
            {
                var callAddress = new CallAddress { Id = "address" };
                flowchart.Steps.Add(callAddress);
                flowchart.Connections.Add(new Connection(new Endpoint(trigger), new Endpoint(callAddress)));
                flowchart.Connections.Add(new Connection(new Endpoint(callAddress), new Endpoint(wait)));
            }
            else
            {
                flowchart.Connections.Add(new Connection(new Endpoint(trigger), new Endpoint(wait)));
            }

            flowchart.Connections.Add(new Connection(new Endpoint(wait, WaitForForm.Submitted), new Endpoint(submitted)));
            flowchart.Connections.Add(new Connection(new Endpoint(wait, PipelineCalls.TimedOut), new Endpoint(timedOut)));
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(flowchart);
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            return definition.DefinitionId;
        }

        private static async Task<PipelineInstance> RunOf(IServiceProvider services, string definitionId) =>
            Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }));

        private static async Task<string> TokenOf(IServiceProvider services, string definitionId)
        {
            var kept = (await RunOf(services, definitionId)).PipelineState.Properties[CallAddress.TokenProperty];
            return kept is System.Text.Json.JsonElement element ? element.GetString()! : (string)kept;
        }

        private static async Task<string?> OutputsOf(IServiceProvider services, string runId, string stepId)
        {
            var steps = await new ListRunStepsQueryHandler(services.GetRequiredService<IStepExecutionStore>(), services.GetRequiredService<IStepRegistry>(), new Author())
                .Handle(new ListRunStepsQuery(runId), CancellationToken.None);
            return steps.Single(step => step.StepId == stepId).OutputsJson;
        }

        private sealed class Author : Stomme.Security.Abstractions.IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        [Fact]
        public async Task The_form_address_shows_the_form_and_a_submission_resumes_the_run_once_with_the_answers()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "details", new WaitForForm { Title = new("Your details"), Description = new("So we can send the order."), Fields = new(Fields), SubmitLabel = new("Send them") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("details"));

            var run = await RunOf(services, definitionId);
            Assert.Equal(PipelineStatus.Running, run.Status);
            var token = await TokenOf(services, definitionId);
            Assert.EndsWith("/" + PipelineCalls.Path + token + "/form\"", (await OutputsOf(services, run.Id, "address"))!.Split(',').Single(part => part.Contains("FormAddress")).Trim().TrimEnd('}').Trim());

            // The form the run waits on, found by the token - and nothing for another token, or for text that is no token.
            var waits = services.GetRequiredService<PipelineFormWaits>();
            var form = (await waits.FindAsync(token, CancellationToken.None))!;
            Assert.Equal(("Your details", "So we can send the order.", "Send them"), (form.Title, form.Description, form.SubmitLabel));
            Assert.Equal((false, null), (form.LeadsOn, form.StartedBy)); // a wait on a form, not a page of one
            Assert.Equal(new[] { "name", "email" }, form.Fields.Select(PipelineForms.KeyOf));
            Assert.Null(await waits.FindAsync(PipelineCalls.NewToken(), CancellationToken.None));
            Assert.Null(await waits.FindAsync("not-a-token", CancellationToken.None));
            var bookmark = Assert.Single(await services.GetRequiredService<IBookmarkStore>().FindManyAsync(new BookmarkFilter { Name = WaitForForm.StepTypeName }));
            Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(bookmark.Payload)); // the token's hash only

            await waits.SubmitAsync(token, """{"name":"Ada","email":"ada@example.com"}""", null, CancellationToken.None);
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, definitionId)).Status);
            Assert.Contains("submitted details", _console.ToString());
            Assert.Contains("ada@example.com", await OutputsOf(services, run.Id, "wait-form"));

            // Once: the form is closed now.
            Assert.Null(await waits.FindAsync(token, CancellationToken.None));
        }

        [Fact]
        public async Task A_form_nobody_fills_in_in_time_goes_down_timed_out_and_closes()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "late", new WaitForForm { Fields = new(Fields), TimeOutAfter = new("10 minutes") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("late"));
            var token = await TokenOf(services, definitionId);
            var waits = services.GetRequiredService<PipelineFormWaits>();
            Assert.Equal("A form", (await waits.FindAsync(token, CancellationToken.None))!.Title);

            _clock.UtcNow = _clock.UtcNow.AddMinutes(9);
            await services.GetRequiredService<DueWaitsTask>().ExecuteAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Running, (await RunOf(services, definitionId)).Status);

            _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
            await services.GetRequiredService<DueWaitsTask>().ExecuteAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, definitionId)).Status);
            Assert.Contains("timed out late", _console.ToString());
            Assert.Null(await waits.FindAsync(token, CancellationToken.None));
        }

        [Fact]
        public async Task A_wait_with_no_call_address_or_no_form_fails_saying_why()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var noAddress = await PublishAsync(services, "no-address", new WaitForForm { Fields = new(Fields) }, address: false);
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("no-address"));
            var run = await RunOf(services, noAddress);
            Assert.Equal(PipelineSubStatus.Faulted, run.SubStatus);
            Assert.Contains(run.PipelineState.Incidents, incident => incident.Message.Contains("Place a 'Call address' step before 'Wait for a form'"));

            var noForm = await PublishAsync(services, "no-form", new WaitForForm { Fields = new("[]") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("no-form"));
            run = await RunOf(services, noForm);
            Assert.Equal(PipelineSubStatus.Faulted, run.SubStatus);
            Assert.Contains(run.PipelineState.Incidents, incident => incident.Message.StartsWith("The form's fields:"));
        }
    }
}
