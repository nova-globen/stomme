#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Mediator.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;
using Stomme.Engine.Pipelines.Runtime.Stores;
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
    /// n8n's multi-page forms (its Form node's next page): a Wait for a form with Next page takes the person who sent the
    /// form before it straight on to it - the Form trigger's submission, then each page's - through the module's own receivers, the
    /// run's form address made with no Call address; the last page's submission is thanked as soon as the run ends, not after the
    /// wait; a form with no next page is not waited on; and a next page whose run could not be stored is never shown.
    /// </summary>
    public sealed class FormNextPageTests
    {
        private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

        private static string Fields(string label, string name) => PipelineForms.Serialize(new[]
        {
            new PipelineFormField { Label = label, Type = PipelineFormFieldTypes.Text, Required = true, Name = name },
        });

        private static async Task<ServiceProvider> HostAsync(StringWriter console, Action<IServiceCollection>? more = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(new PipelineTriggerOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddScoped<PipelineFormWaits>();
            services.AddScoped<PipelineFormReceiver>();
            services.AddSingleton<PipelineFormContinuations>();
            services.AddNotificationHandler<FormContinuationOnCommit, PipelineStateCommitted>();
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<IStimulusDispatcher, InlineStimulusDispatcher>(); // the host's dispatcher works in the background
            more?.Invoke(services);
            var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
            return provider;
        }

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private sealed class InlineStimulusDispatcher(IStimulusSender sender) : IStimulusDispatcher
        {
            public async Task<DispatchStimulusResponse> SendAsync(DispatchStimulusRequest request, CancellationToken cancellationToken = default)
            {
                await sender.SendAsync(request.StepTypeName!, request.Stimulus!, request.Metadata, cancellationToken);
                return DispatchStimulusResponse.Empty;
            }
        }

        // A run whose bookmarks cannot be stored: the store refuses the burst that adds one.
        private sealed class RefusingBookmarksPersister : IBookmarksPersister
        {
            public Task PersistBookmarksAsync(UpdateBookmarksRequest updateBookmarksRequest) =>
                updateBookmarksRequest.Diff.Added.Count > 0 ? throw new InvalidOperationException("the store is down") : Task.CompletedTask;
        }

        // The memory store lists triggers in its dictionary's order, which their random ids decide - so a test that needs an order sets
        // it: this one keeps the memory store and lists what it holds in the order it was indexed. (Publishing until the order came out
        // right left a pipeline published second still listed first after 20 more in 109 of 14,253 runs - measured 2026-10-18.)
        private sealed class InIndexingOrderTriggerStore(MemoryTriggerStore store) : ITriggerStore
        {
            private readonly ConcurrentDictionary<string, long> _indexedAt = new();
            private long _indexed;

            private List<StoredTrigger> Indexed(IEnumerable<StoredTrigger> triggers)
            {
                var indexed = triggers.ToList();
                foreach (var trigger in indexed)
                {
                    _indexedAt.TryAdd(trigger.Id, Interlocked.Increment(ref _indexed));
                }

                return indexed;
            }

            private List<StoredTrigger> InOrder(IEnumerable<StoredTrigger> triggers) =>
                triggers.OrderBy(trigger => _indexedAt.GetValueOrDefault(trigger.Id, long.MaxValue)).ToList();

            public ValueTask SaveAsync(StoredTrigger record, CancellationToken cancellationToken = default) => store.SaveAsync(Indexed(new[] { record }).Single(), cancellationToken);

            public ValueTask SaveManyAsync(IEnumerable<StoredTrigger> records, CancellationToken cancellationToken = default) => store.SaveManyAsync(Indexed(records), cancellationToken);

            public ValueTask ReplaceAsync(IEnumerable<StoredTrigger> removed, IEnumerable<StoredTrigger> added, CancellationToken cancellationToken = default) =>
                store.ReplaceAsync(removed, Indexed(added), cancellationToken);

            public ValueTask<long> DeleteManyAsync(TriggerFilter filter, CancellationToken cancellationToken = default) => store.DeleteManyAsync(filter, cancellationToken);

            public async ValueTask<StoredTrigger?> FindAsync(TriggerFilter filter, CancellationToken cancellationToken = default) =>
                InOrder(await store.FindManyAsync(filter, cancellationToken)).FirstOrDefault();

            public async ValueTask<IEnumerable<StoredTrigger>> FindManyAsync(TriggerFilter filter, CancellationToken cancellationToken = default) =>
                InOrder(await store.FindManyAsync(filter, cancellationToken));

            // A page is asked for in an order of its own.
            public ValueTask<Page<StoredTrigger>> FindManyAsync(TriggerFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default) =>
                store.FindManyAsync(filter, pageArgs, cancellationToken);

            public ValueTask<Page<StoredTrigger>> FindManyAsync<TProp>(TriggerFilter filter, PageArgs pageArgs, StoredTriggerOrder<TProp> order, CancellationToken cancellationToken = default) =>
                store.FindManyAsync(filter, pageArgs, order, cancellationToken);
        }

        // The Form trigger "signup", then its pages - each a Wait for a form - then a line when it is done.
        private static async Task<string> PublishAsync(IServiceProvider services, params WaitForForm[] pages)
        {
            var trigger = new Form { Id = "trigger", FormName = new("signup"), Title = new("Sign up"), Fields = new(Fields("Your name", "name")) };
            trigger.SetCanStartPipeline(true);
            var done = new WriteLine("signed up") { Id = "done" };
            var flowchart = new Flowchart { Id = "flow", Steps = { trigger } };
            IStep previous = trigger;
            for (var at = 0; at < pages.Length; at++)
            {
                pages[at].Id = "page-" + (at + 2);
                flowchart.Steps.Add(pages[at]);
                flowchart.Connections.Add(new Connection(previous is WaitForForm wait ? new Endpoint(wait, WaitForForm.Submitted) : new Endpoint(previous), new Endpoint(pages[at])));
                previous = pages[at];
            }

            flowchart.Steps.Add(done);
            flowchart.Connections.Add(new Connection(previous is WaitForForm last ? new Endpoint(last, WaitForForm.Submitted) : new Endpoint(previous), new Endpoint(done)));
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(flowchart);
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            return definition.DefinitionId;
        }

        private static async Task<PipelineInstance> RunOf(IServiceProvider services, string definitionId) =>
            Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }));

        private static WaitForForm Page(string title, string label, string name, bool nextPage = true) =>
            new() { Title = new(title), Fields = new(Fields(label, name)), NextPage = new(nextPage) };

        [Fact]
        public async Task Each_page_takes_the_person_on_to_the_next_and_the_last_thanks_them_as_soon_as_the_run_ends()
        {
            var console = new StringWriter();
            await using var provider = await HostAsync(console);
            using var scope = provider.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, Page("Page two", "Your e-mail", "email"), Page("Page three", "Your plan", "plan"));
            var receiver = services.GetRequiredService<PipelineFormReceiver>();
            var waits = services.GetRequiredService<PipelineFormWaits>();
            var continuations = services.GetRequiredService<PipelineFormContinuations>();

            // The trigger's form says a next page can follow - read from the published pipeline.
            var form = (await receiver.FindAsync("signup", CancellationToken.None))!;
            Assert.True(form.LeadsOn);

            // Its submission is taken on to page two: the run's form address, made with no Call address in the pipeline.
            var next = await continuations.SendAsync(form.LeadsOn, Within, id => receiver.SubmitAsync("signup", """{"name":"Ada"}""", id, CancellationToken.None), CancellationToken.None);
            var run = await RunOf(services, definitionId);
            var token = run.PipelineState.Properties[CallAddress.TokenProperty] is System.Text.Json.JsonElement element ? element.GetString() : (string)run.PipelineState.Properties[CallAddress.TokenProperty];
            Assert.Equal(token, next);
            var two = (await waits.FindAsync(next!, CancellationToken.None))!;
            Assert.Equal(("Page two", true, "signup"), (two.Title, two.LeadsOn, two.StartedBy)); // the form it follows, whose thank-you ends it

            // Page two's submission is taken on to page three - the same address, now showing the next form.
            next = await continuations.SendAsync(two.LeadsOn, Within, id => waits.SubmitAsync(token!, """{"email":"ada@example.com"}""", id, CancellationToken.None), CancellationToken.None);
            Assert.Equal(token, next);
            var three = (await waits.FindAsync(token!, CancellationToken.None))!;
            Assert.Equal(("Page three", "signup"), (three.Title, three.StartedBy)); // a later burst's page still knows it - kept on the run

            // The last page: the run ends, and the person is thanked at once - not after the wait.
            var clock = Stopwatch.StartNew();
            next = await continuations.SendAsync(three.LeadsOn, Within, id => waits.SubmitAsync(token!, """{"plan":"Pro"}""", id, CancellationToken.None), CancellationToken.None);
            Assert.Null(next);
            Assert.True(clock.Elapsed < Within / 2, $"thanked after {clock.Elapsed.TotalSeconds:0.0} s - it waited for a next page the run never reached");
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, definitionId)).Status);
            Assert.Contains("signed up", console.ToString());
            Assert.Null(await waits.FindAsync(token!, CancellationToken.None));
        }

        [Fact]
        public async Task A_form_no_next_page_follows_is_not_waited_on_and_a_ticked_page_needs_no_call_address()
        {
            var console = new StringWriter();
            await using var provider = await HostAsync(console);
            using var scope = provider.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, Page("Not a page", "Anything", "anything", nextPage: false));
            var receiver = services.GetRequiredService<PipelineFormReceiver>();
            var form = (await receiver.FindAsync("signup", CancellationToken.None))!;
            Assert.False(form.LeadsOn);

            // Not waited on: sent on with no id, and the run - a wait with no address and no Next page - fails saying what to do.
            var sent = 0;
            Assert.Null(await services.GetRequiredService<PipelineFormContinuations>().SendAsync(form.LeadsOn, Within, id =>
            {
                Assert.Null(id);
                sent++;
                return receiver.SubmitAsync("signup", """{"name":"Ada"}""", id, CancellationToken.None);
            }, CancellationToken.None));
            Assert.Equal(1, sent);
            var run = Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter()));
            Assert.Equal(PipelineSubStatus.Faulted, run.SubStatus);
            Assert.Contains(run.PipelineState.Incidents, incident => incident.Message.Contains("or tick Next page"));

            // A run whose pipeline has no next page never answers a submission waiting for one: another pipeline on the form may have it.
            var continuations = services.GetRequiredService<PipelineFormContinuations>();
            var (id, next) = continuations.Register();
            await receiver.SubmitAsync("signup", """{"name":"Grace"}""", id, CancellationToken.None);
            Assert.Equal(2, (await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter())).Count());
            Assert.False(next.IsCompleted);
            continuations.Forget(id);
        }

        [Fact]
        public async Task A_form_two_pipelines_share_takes_the_person_on_to_the_one_with_a_next_page()
        {
            var console = new StringWriter();
            await using var provider = await HostAsync(console, services => services.AddSingleton<ITriggerStore>(sp =>
                new InIndexingOrderTriggerStore(new MemoryTriggerStore(sp.GetRequiredService<Stomme.Engine.Common.Services.MemoryStore<StoredTrigger>>()))));
            using var scope = provider.CreateScope();
            var services = scope.ServiceProvider;
            var plain = await PublishAsync(services); // the form, then done
            var paged = await PublishAsync(services, Page("Page two", "Your e-mail", "email"));

            // Listed in the order they were published - the one with no next page first, so "a next page can follow" cannot be read
            // from the first alone.
            var listed = await services.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = PipelineForms.StepType });
            Assert.Equal(new[] { plain, paged }, listed.Select(trigger => trigger.PipelineDefinitionId));
            var receiver = services.GetRequiredService<PipelineFormReceiver>();
            var form = (await receiver.FindAsync("signup", CancellationToken.None))!;
            Assert.True(form.LeadsOn); // one of them has a next page

            // The pipeline with no next page ends first, and must not thank the person before the other shows its page.
            var next = await services.GetRequiredService<PipelineFormContinuations>().SendAsync(form.LeadsOn, Within, id => receiver.SubmitAsync("signup", """{"name":"Ada"}""", id, CancellationToken.None), CancellationToken.None);
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, plain)).Status);
            Assert.NotNull(next);
            Assert.Equal("Page two", (await services.GetRequiredService<PipelineFormWaits>().FindAsync(next!, CancellationToken.None))!.Title);
            Assert.Equal(PipelineStatus.Running, (await RunOf(services, paged)).Status);
        }

        // BL-229, n8n's redirect after a submission: the form's address to go to read from the published pipeline; one that is not
        // http(s) refused when it is published, naming it.
        [Fact]
        public async Task A_forms_address_to_go_to_is_read_from_the_published_form_and_a_bad_one_refused_at_publish()
        {
            var console = new StringWriter();
            await using var provider = await HostAsync(console);
            using var scope = provider.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();

            async Task<(bool Succeeded, string Errors)> PublishFormAsync(string name, string redirect)
            {
                var trigger = new Form { Id = "trigger", FormName = new(name), Title = new("Sign up"), Fields = new(Fields("Your name", "name")), Redirect = new(redirect) };
                trigger.SetCanStartPipeline(true);
                var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { trigger } });
                var published = await publisher.PublishAsync(definition);
                return (published.Succeeded, string.Join("; ", published.ValidationErrors.Select(error => error.Message)));
            }

            Assert.True((await PublishFormAsync("welcome", " https://example.com/welcome ")).Succeeded);
            Assert.Equal("https://example.com/welcome", (await services.GetRequiredService<PipelineFormReceiver>().FindAsync("welcome", CancellationToken.None))!.Redirect);

            // The save refuses it first, in its own words - as it refuses a form name that cannot be an address - and the engine, which
            // indexes the form, refuses it again should anything get past.
            var graph = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "form", Type = PipelineForms.StepType, Name = "Form", Inputs = { ["FormName"] = "sneaky", ["Title"] = "Sneaky", ["Redirect"] = "javascript:alert(1)" } } },
            };
            var refusal = Assert.Throws<Stomme.Modules.Pipelines.Application.PipelineGraphRefusedException>(() => PipelineGraphJudge.Body(graph, null, services.GetRequiredService<IStepRegistry>(),
                services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>()));
            Assert.Equal("Then go to 'javascript:alert(1)' is not an https:// or http:// address.", refusal.Message);
            Assert.False((await PublishFormAsync("sneaky", "javascript:alert(1)")).Succeeded);
        }

        [Fact]
        public async Task A_next_page_whose_run_could_not_be_stored_is_never_shown()
        {
            var console = new StringWriter();
            await using var provider = await HostAsync(console, services => services.AddScoped<IBookmarksPersister, RefusingBookmarksPersister>());
            using var scope = provider.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, Page("Page two", "Your e-mail", "email"));
            var continuations = services.GetRequiredService<PipelineFormContinuations>();

            // The run reaches page two, and the store refuses it: the submission is not taken on to a page that is not there.
            var (id, next) = continuations.Register();
            await Assert.ThrowsAnyAsync<Exception>(() => services.GetRequiredService<PipelineFormReceiver>().SubmitAsync("signup", """{"name":"Ada"}""", id, CancellationToken.None));
            Assert.False(next.IsCompleted);
            continuations.Forget(id);
        }
    }
}
