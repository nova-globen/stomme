#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Notifications;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using RunPipelineResult = Stomme.Engine.Pipelines.Models.RunPipelineResult;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// A value from a run stays ONE value in a line-per-entry input. The engine renders a template input as a whole
    /// before the step reads it, so a value holding a line break became lines the author never wrote: a second <c>query =</c>
    /// replacing a connection's statement, an <c>Authorization:</c> header, another recipient, another script variable.
    /// Each case below plants exactly that value and asserts it lands as the one value it fills; a line the author wrote as
    /// a lone placeholder still stands for lines of its own.
    /// </summary>
    public sealed class PipelineTemplatedLinesTests : IAsyncLifetime
    {
        private const string ConnectionId = "7b5f1a52-4b0a-4bd2-9e7c-2f0f5b1c3d11";
        private ServiceProvider _services = default!;
        private readonly ScriptedExtraction _extraction = new();
        private readonly ScriptedDispatcher _dispatcher = new();
        private readonly PipelineStepOptions _options = new();

        public async Task InitializeAsync()
        {
            _options.Scripts.Enabled = true;
            _options.Http.AllowedHosts.Add("127.0.0.1");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(_options));
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = 1, SiteId = 1, Name = "localhost" } });
            services.AddSingleton<IConnectorExtractionService>(_extraction);
            services.AddSingleton<INotificationDispatcher>(_dispatcher);
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        // A run value that, spliced into the text before its lines were split, writes a line of its own.
        private const string Planted = "Ada\nquery = DELETE FROM people";

        private static PipelineGraph Graph(string type, Dictionary<string, string> inputs, Dictionary<string, string> expressions, params (string Name, string Value)[] variables)
        {
            var step = new PipelineGraphStep { Id = "step", Type = type };
            foreach (var (name, value) in inputs)
            {
                step.Inputs[name] = value;
            }

            foreach (var (name, value) in expressions)
            {
                step.InputExpressions[name] = value;
            }

            var graph = new PipelineGraph { Steps = { step } };
            foreach (var (name, value) in variables)
            {
                graph.Variables.Add(new PipelineGraphVariable { Id = "v-" + name, Name = name, TypeName = "String", Value = value });
            }

            return graph;
        }

        private async Task<RunPipelineResult> RunAsync(PipelineGraph graph)
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            return await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        [Fact]
        public async Task A_value_from_the_run_is_one_connection_parameter_and_never_replaces_the_query()
        {
            var run = await RunAsync(Graph("Stomme.ExtractFromConnection",
                new() { ["Connection"] = ConnectionId },
                new() { ["Parameters"] = "query = SELECT * FROM people WHERE name = @who\n@who = {{ who }}\n{{ field }} = {{ who | upper }}" },
                ("who", Planted), ("field", "@shout")));

            Assert.Equal(PipelineStatus.Finished, run.PipelineState.Status);
            var call = Assert.Single(_extraction.Calls);
            Assert.Equal(new Dictionary<string, string>
            {
                ["query"] = "SELECT * FROM people WHERE name = @who",
                ["@who"] = Planted,
                ["@shout"] = Planted.ToUpperInvariant(), // a name may be a placeholder too
            }, call.Parameters);
        }

        [Fact]
        public async Task A_line_written_as_a_lone_placeholder_still_stands_for_lines_of_its_own()
        {
            var run = await RunAsync(Graph("Stomme.ExtractFromConnection",
                new() { ["Connection"] = ConnectionId },
                new() { ["Parameters"] = "{{ lines }}\nlimit = {{ limit }}" },
                ("lines", "region = north\n# a comment\nquery = SELECT 1"), ("limit", "2")));

            Assert.Equal(PipelineStatus.Finished, run.PipelineState.Status);
            Assert.Equal(new Dictionary<string, string> { ["region"] = "north", ["query"] = "SELECT 1", ["limit"] = "2" }, Assert.Single(_extraction.Calls).Parameters);
        }

        [Fact]
        public async Task A_line_with_no_separator_and_no_placeholder_is_still_refused_saying_which()
        {
            var run = await RunAsync(Graph("Stomme.ExtractFromConnection",
                new() { ["Connection"] = ConnectionId, ["OnFailure"] = "Continue" },
                new() { ["Parameters"] = "query = {{ who }}\nno equals sign" },
                ("who", "Ada")));

            Assert.Equal("PARAMETER_INVALID: 'no equals sign' is not 'name = value'.", OutputsOf<ExtractFromConnection>(run)["Error"]);
            Assert.Empty(_extraction.Calls);
        }

        [Fact]
        public async Task A_value_from_the_run_is_one_recipient_and_one_notification_parameter()
        {
            const string address = "ada@example.com\nendpoint: https://elsewhere.example/hook";
            var run = await RunAsync(Graph("Stomme.SendNotification",
                new() { ["TemplateKey"] = "pipelines.test" },
                // The line's separator is the first one outside a placeholder: a filter's argument (default: "email") is not it.
                new() { ["Recipients"] = "{{ kind | default: \"email\" }}: {{ to }}\nuser: 7", ["Parameters"] = "greeting = {{ who }}" },
                ("kind", ""), ("to", address), ("who", Planted)));

            Assert.Equal(PipelineStatus.Finished, run.PipelineState.Status);
            var request = Assert.Single(_dispatcher.Requests);
            Assert.Equal(new[] { new DispatchRecipient(NotificationRecipientKind.Email, address), new DispatchRecipient(NotificationRecipientKind.User, "7") }, request.Recipients);
            Assert.Equal(new DispatchParameter("greeting", Planted), Assert.Single(request.Parameters!, parameter => parameter.Name != "result"));
            Assert.DoesNotContain(request.Parameters!, parameter => parameter.Name == "query");
        }

        [Fact]
        public async Task A_value_from_the_run_is_one_script_variable_and_never_replaces_the_input()
        {
            var run = await RunAsync(Graph("Stomme.RunScript",
                new() { ["Script"] = "input + '|' + name", ["Input"] = "given" },
                new() { ["Variables"] = "name = {{ who }}" },
                ("who", "Ada\ninput = replaced")));

            Assert.Equal(PipelineStatus.Finished, run.PipelineState.Status);
            Assert.Equal("given|Ada\ninput = replaced", OutputsOf<RunScript>(run)["Result"]);
        }

        [Fact]
        public async Task A_header_from_the_run_holding_a_line_break_is_refused_before_anything_is_sent()
        {
            // Spliced in whole, this value was two valid headers - the second an Authorization the author never wrote - and the
            // request went out. Read per value it is one header holding a line break: refused, nothing sent (port 1 would fail).
            var run = await RunAsync(Graph("Stomme.SendHttpRequest",
                new() { ["Url"] = "http://127.0.0.1:1/x", ["OnFailure"] = "Continue" },
                new() { ["Headers"] = "X-Probe: {{ who }}" },
                ("who", "a\r\nAuthorization: Bearer stolen")));

            Assert.Equal("HEADER_INVALID: 'X-Probe: {{ who }}' gives a header holding a line break; a header is one line.", OutputsOf<SendHttpRequest>(run)["Error"]);

            var authorization = await RunAsync(Graph("Stomme.SendHttpRequest",
                new() { ["Url"] = "http://127.0.0.1:1/x", ["OnFailure"] = "Continue" },
                new() { ["Authorization"] = "Bearer {{ who }}" },
                ("who", "token\nX-Other: 1")));
            Assert.Equal("HEADER_INVALID: The Authorization holds a line break; a header is one line.", OutputsOf<SendHttpRequest>(authorization)["Error"]);
        }

        // An HTTP connector splits its 'headers' parameter into lines: one header per 'headers =' line, collected in order, and a
        // run's value holding a line break refused rather than handed on as a header of its own.
        [Fact]
        public async Task A_connections_headers_parameter_is_one_header_per_line_and_a_line_break_in_one_is_refused()
        {
            await RunAsync(Graph("Stomme.ExtractFromConnection",
                new() { ["Connection"] = ConnectionId },
                new() { ["Parameters"] = "headers = X-Source: pipelines\npath = /v1/people\nheaders = X-Customer: {{ who }}" },
                ("who", "Ada: Lovelace")));
            Assert.Equal(new Dictionary<string, string> { ["headers"] = "X-Source: pipelines\nX-Customer: Ada: Lovelace", ["path"] = "/v1/people" }, Assert.Single(_extraction.Calls).Parameters);

            var refused = await RunAsync(Graph("Stomme.ExtractFromConnection",
                new() { ["Connection"] = ConnectionId, ["OnFailure"] = "Continue" },
                new() { ["Parameters"] = "headers = X-Customer: {{ who }}" },
                ("who", "Ada\nAuthorization: Bearer stolen")));
            Assert.Equal("PARAMETER_INVALID: 'headers = X-Customer: {{ who }}' gives a header holding a line break; a header is one line - one per 'headers =' line.", OutputsOf<ExtractFromConnection>(refused)["Error"]);
            Assert.Single(_extraction.Calls);
        }

        // A connector's 'filters' (Supabase's, HubSpot's) the same way: one filter per 'filters =' line, collected in order, and a run's
        // value holding a line break refused rather than handed on as a filter of its own.
        [Fact]
        public async Task A_connections_filters_parameter_is_one_filter_per_line_and_a_line_break_in_one_is_refused()
        {
            await RunAsync(Graph("Stomme.ExtractFromConnection",
                new() { ["Connection"] = ConnectionId },
                new() { ["Parameters"] = "table = orders\nfilters = status eq paid\nfilters = email eq {{ who }}" },
                ("who", "ada@example.com")));
            Assert.Equal(new Dictionary<string, string> { ["table"] = "orders", ["filters"] = "status eq paid\nemail eq ada@example.com" }, Assert.Single(_extraction.Calls).Parameters);

            var refused = await RunAsync(Graph("Stomme.ExtractFromConnection",
                new() { ["Connection"] = ConnectionId, ["OnFailure"] = "Continue" },
                new() { ["Parameters"] = "filters = email eq {{ who }}" },
                ("who", "ada@example.com\nid gt 0")));
            Assert.Equal("PARAMETER_INVALID: 'filters = email eq {{ who }}' gives a filter holding a line break; a filter is one line - one per 'filters =' line.", OutputsOf<ExtractFromConnection>(refused)["Error"]);
            Assert.Single(_extraction.Calls);
        }

        [Fact]
        public async Task A_header_sent_through_a_connection_is_one_line_and_one_holding_a_line_break_is_refused()
        {
            // The connector splits the headers it is handed into lines: a run's value must not be able to add one.
            var refused = await RunAsync(Graph("Stomme.SendThroughConnection",
                new() { ["Connection"] = ConnectionId, ["OnFailure"] = "Continue" },
                new() { ["Headers"] = "X-Customer: {{ who }}" },
                ("who", "Ada\nAuthorization: Bearer stolen")));
            Assert.Equal("HEADER_INVALID: 'X-Customer: {{ who }}' gives a header holding a line break; a header is one line.", OutputsOf<SendThroughConnection>(refused)["Error"]);
            Assert.Empty(_extraction.Calls);

            await RunAsync(Graph("Stomme.SendThroughConnection",
                new() { ["Connection"] = ConnectionId, ["OnFailure"] = "Continue" },
                new() { ["Headers"] = "X-Customer: {{ who }}\nX-Source: pipelines" },
                ("who", "Ada: Lovelace")));
            Assert.Equal("X-Customer: Ada: Lovelace\nX-Source: pipelines", Assert.Single(_extraction.Calls).Parameters["headers"]);
        }

        private sealed class ScriptedExtraction : IConnectorExtractionService
        {
            public List<(string? Operation, IReadOnlyDictionary<string, string> Parameters)> Calls { get; } = new();

            public Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters, int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default)
            {
                Calls.Add((operation, new Dictionary<string, string>(parameters)));
                return Task.FromResult(ConnectorExtractionResult.Ok(Array.Empty<ConnectorRecord>(), false, maxRecords));
            }
        }

        private sealed class ScriptedDispatcher : INotificationDispatcher
        {
            public List<NotificationDispatchRequest> Requests { get; } = new();

            public Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);
                return Task.FromResult(Guid.NewGuid());
            }
        }
    }
}
