#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - Respond to webhook (n8n's Respond to Webhook): a Webhook set to answer from the pipeline is indexed under a
    /// stimulus of its own, so every webhook already published keeps its hash; a call to the name starts every pipeline
    /// listening on it and, when one answers from the pipeline, waits for its Respond to webhook step - its status, type and
    /// body - up to the configured time, after which it is accepted and the run goes on. A Respond step no call waits for
    /// answers nobody and says so.
    /// </summary>
    public sealed class RespondToWebhookTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly PipelineTriggerOptions _options = new() { Webhooks = { Enabled = true, AnswerWithinSeconds = 3 } };
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-respond-" + Guid.NewGuid().ToString("N"));

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(_options));
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions { Files = { Root = _root, MaxFileBytes = 64 } }));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
            });
            services.AddSingleton<PipelineWebhookAnswers>();
            services.AddScoped<PipelineWebhookReceiver>();
            services.AddExpressionDescriptorProvider<Stomme.Modules.Pipelines.Expressions.TemplateExpressionDescriptorProvider>(); // the starter's expressions
            services.AddScoped<IStimulusDispatcher, InlineStimulusDispatcher>(); // the host's dispatcher works in the background
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
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

        // A webhook on the name, then (when it answers) Respond to webhook, then a line written after it.
        private static async Task PublishAsync(IServiceProvider services, string name, WebhookAnswer answer, bool respond, string tag)
        {
            var trigger = new Webhook { WebhookName = new(name), Answer = new(answer), Id = "trigger-" + tag };
            trigger.SetCanStartPipeline(true);
            var after = new WriteLine("after " + tag) { Id = "after-" + tag };
            var flowchart = new Flowchart { Id = "flow-" + tag, Steps = { trigger, after } };
            if (respond)
            {
                var reply = new RespondToWebhook { Id = "reply-" + tag, StatusCode = new(201), Body = new("{\"ok\":\"" + tag + "\"}") };
                flowchart.Steps.Add(reply);
                flowchart.Connections.Add(new Connection(new Endpoint(trigger), new Endpoint(reply)));
                flowchart.Connections.Add(new Connection(new Endpoint(reply), new Endpoint(after)));
            }
            else
            {
                flowchart.Connections.Add(new Connection(new Endpoint(trigger), new Endpoint(after)));
            }

            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(flowchart);
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
        }

        private static Task<PipelineWebhookAnswer?> CallAsync(IServiceProvider services, string name) =>
            services.GetRequiredService<PipelineWebhookReceiver>().ReceiveAsync(name, "{\"order\":7}", "application/json", null, CancellationToken.None);

        [Fact]
        public async Task A_call_waits_for_the_pipelines_answer_and_the_run_goes_on_after_it()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, "orders", WebhookAnswer.FromThePipeline, respond: true, "answering");
            await PublishAsync(services, "orders", WebhookAnswer.Immediately, respond: false, "plain");

            var answer = await CallAsync(services, "orders");
            Assert.Equal(new PipelineWebhookAnswer(201, "application/json", "{\"ok\":\"answering\"}"), answer);
            // Each pipeline started ONCE - the answering one went on past its answer, the one that answers at once ran too.
            Assert.Single(_console.ToString().Split("after answering").Skip(1));
            Assert.Single(_console.ToString().Split("after plain").Skip(1));
        }

        [Fact]
        public async Task A_name_no_pipeline_answers_on_is_accepted_at_once()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, "quick", WebhookAnswer.Immediately, respond: false, "quick");

            var clock = Stopwatch.StartNew();
            Assert.Null(await CallAsync(services, "quick"));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2.5), clock.Elapsed.ToString()); // not the 3 seconds an answer is waited for
            Assert.Single(_console.ToString().Split("after quick").Skip(1));

            // Answering at once keeps the stimulus every webhook was published under, so none needs publishing again - and the
            // answering stimulus hashes apart from it (the hash is of the properties alone: a Name-only record matched both).
            var hasher = services.GetRequiredService<IStimulusHasher>();
            Assert.NotEqual(hasher.Hash(Webhook.StepTypeName, new WebhookStimulus("quick")), hasher.Hash(Webhook.StepTypeName, new AnsweredWebhookStimulus("quick")));
            var stored = await services.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = Webhook.StepTypeName });
            Assert.Contains(stored, trigger => trigger.Hash == hasher.Hash(Webhook.StepTypeName, new WebhookStimulus("quick")));
        }

        // The starter, through the codec as the editor saves it: the caller gets its quote, worked out with filters.
        [Fact]
        public async Task The_answer_a_call_starter_answers_its_caller_with_the_quote()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var graph = Stomme.Models.Pipelines.PipelineStarters.Find("answer-a-call")!.Graph();
            var body = Stomme.Modules.Pipelines.Application.PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);

            var answer = await services.GetRequiredService<PipelineWebhookReceiver>().ReceiveAsync("quote", "{\"item\":\"tea\",\"quantity\":3}", "application/json", null, CancellationToken.None);
            Assert.NotNull(answer);
            Assert.Equal((200, "application/json"), (answer!.StatusCode, answer.ContentType));
            var quote = System.Text.Json.JsonDocument.Parse(answer.Body).RootElement;
            Assert.Equal("tea", quote.GetProperty("item").GetString());
            Assert.Equal(28.50m, quote.GetProperty("price").GetDecimal()); // 3 x 9.5, to two decimals
        }

        // A webhook answering from the pipeline with the Respond step given.
        private static async Task PublishAnswerAsync(IServiceProvider services, string name, RespondToWebhook reply)
        {
            var trigger = new Webhook { WebhookName = new(name), Answer = new(WebhookAnswer.FromThePipeline), Id = "trigger-" + name };
            trigger.SetCanStartPipeline(true);
            reply.Id = "reply-" + name;
            var flowchart = new Flowchart { Id = "flow-" + name, Steps = { trigger, reply }, Connections = { new Connection(new Endpoint(trigger), new Endpoint(reply)) } };
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            Assert.True((await publisher.PublishAsync(await publisher.NewAsync(flowchart))).Succeeded);
        }

        // BL-229, n8n's Respond to Webhook headers and binary file: the headers the answer carries, a file of the pipeline folder
        // answered with its type from its name and a name to save it under; the site's own headers, a line that is no header, a file
        // outside the folder, one missing and one too large refused - the run faults, nothing is answered.
        [Fact]
        public async Task An_answer_carries_headers_and_a_file_and_the_sites_own_headers_are_refused()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            Directory.CreateDirectory(Path.Combine(_root, "default", "invoices"));
            var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 invoice 7");
            File.WriteAllBytes(Path.Combine(_root, "default", "invoices", "7.pdf"), pdf);

            await PublishAnswerAsync(services, "cors", new RespondToWebhook { Body = new("{\"ok\":true}"), Headers = new("Access-Control-Allow-Origin: *\nCache-Control: no-store") });
            var withHeaders = await CallAsync(services, "cors");
            Assert.Equal(("application/json", "{\"ok\":true}", null), (withHeaders!.ContentType, withHeaders.Body, withHeaders.File));
            Assert.Equal(new[] { ("Access-Control-Allow-Origin", "*"), ("Cache-Control", "no-store") }, withHeaders.Headers);

            await PublishAnswerAsync(services, "invoice", new RespondToWebhook { StatusCode = new(201), File = new("invoices/7.pdf"), DownloadAs = new("invoice-7.pdf"), Headers = new("X-Invoice: 7") });
            var file = await CallAsync(services, "invoice");
            Assert.Equal((201, "application/pdf", "invoice-7.pdf"), (file!.StatusCode, file.ContentType, file.DownloadName));
            Assert.Equal(pdf, file.File);
            Assert.Equal(new[] { ("X-Invoice", "7") }, file.Headers);

            async Task<string> Refusal(RespondToWebhook reply)
            {
                var run = await services.GetRequiredService<IPipelineRunner>().RunAsync(reply);
                Assert.Equal(PipelineSubStatus.Faulted, run.PipelineState.SubStatus);
                return run.PipelineState.Incidents.Single().Message;
            }

            Assert.Equal("'Set-Cookie' is the site's to set, not an answer's.", await Refusal(new RespondToWebhook { Headers = new("Set-Cookie: session=1") }));
            Assert.Equal("'content-security-policy' is the site's to set, not an answer's.", await Refusal(new RespondToWebhook { Headers = new("content-security-policy: none") }));
            Assert.Equal("'X Order' is not a header name - letters, digits and '-'.", await Refusal(new RespondToWebhook { Headers = new("X Order: 7") }));
            Assert.Equal("'no colon here' is not a 'Name: value' header.", await Refusal(new RespondToWebhook { Headers = new("no colon here") }));
            Assert.Contains("resolves outside the site's folder", await Refusal(new RespondToWebhook { File = new("../secret.txt") }));
            Assert.Equal("There is no file 'invoices/8.pdf' in the site's pipeline folder.", await Refusal(new RespondToWebhook { File = new("invoices/8.pdf") }));
            File.WriteAllBytes(Path.Combine(_root, "default", "invoices", "big.pdf"), new byte[65]);
            Assert.Equal("'invoices/big.pdf' is 65 bytes; a pipeline file is at most 64.", await Refusal(new RespondToWebhook { File = new("invoices/big.pdf") }));
        }

        [Fact]
        public async Task An_answer_that_does_not_come_in_time_is_accepted_and_one_with_no_caller_answers_nobody()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, "slow", WebhookAnswer.FromThePipeline, respond: false, "slow");

            var clock = Stopwatch.StartNew();
            Assert.Null(await CallAsync(services, "slow")); // no Respond step: waited its 3 seconds, then accepted
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(3), clock.Elapsed.ToString());

            // A Respond step in a run no call waits for: the run finishes, the log says nobody was answered.
            var run = await services.GetRequiredService<IPipelineRunner>().RunAsync(new RespondToWebhook { Body = new("hi") });
            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            Assert.Equal(false, run.Journal.StepExecutionContexts.Single(c => c.Step is RespondToWebhook).GetOutputs()["Answered"]);
        }
    }
}
