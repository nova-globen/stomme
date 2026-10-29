#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Help;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.ContextualHelp.Application;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Pipelines.Application;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// The gateways over the mediator. The reads are the owning modules' gated queries, and a refused
    /// one is NOTHING to the agent; the writes are the modules' gated commands in order, and a failure part way removes what
    /// was created through the module's own delete. The pipeline draft becomes the editor's graph and is judged by the real
    /// engine's catalog and serializer (the engine composed as the host composes it, <c>PipelineGraphCodecTests</c>' way).
    /// </summary>
    public sealed class MediatorAssistantGatewayTests : IAsyncLifetime
    {
        private ServiceProvider _engine = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEngine();
            _engine = services.BuildServiceProvider();
            using var scope = _engine.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _engine.DisposeAsync();

        /// <summary>Answers each request type from a handler the test gives; records every request in order.</summary>
        private sealed class FakeMediator : IMediator
        {
            private readonly Dictionary<Type, Func<object, object?>> _handlers = new();

            public List<object> Sent { get; } = new();

            public FakeMediator On<TRequest>(Func<TRequest, object?> handler)
            {
                _handlers[typeof(TRequest)] = request => handler((TRequest)request);
                return this;
            }

            public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
            {
                Sent.Add(request);
                try
                {
                    return _handlers.TryGetValue(request.GetType(), out var handler)
                        ? Task.FromResult((TResponse)handler(request)!)
                        : throw new InvalidOperationException("No handler for " + request.GetType().Name);
                }
                catch (Exception ex)
                {
                    return Task.FromException<TResponse>(ex);
                }
            }

            public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => throw new NotSupportedException();

            public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        }

        private static DatasetDescriptor Dataset(Guid id, string name, DataClassification classification, params DatasetColumnSummaryDescriptor[] columns) =>
            new(id, name, OriginKind.Upload, "file:1", classification, new DatasetSchemaDescriptor(Array.Empty<DatasetColumnDescriptor>()), new DatasetSummaryDescriptor(columns, 42), DateTime.UtcNow);

        private async Task<IReadOnlyList<StepCatalogEntry>> CatalogAsync()
        {
            using var scope = _engine.CreateScope();
            var provider = scope.ServiceProvider;
            return await new ListStepsQueryHandler(provider.GetRequiredService<IStepRegistry>(), provider.GetRequiredService<IRegistriesPopulator>()).Handle(new ListStepsQuery(), CancellationToken.None);
        }

        private async Task<IReadOnlyList<string>> EngineVerdictAsync(PipelineGraph graph)
        {
            using var scope = _engine.CreateScope();
            var provider = scope.ServiceProvider;
            return await new ValidateGraphQueryHandler(provider.GetRequiredService<IStepRegistry>(), provider.GetRequiredService<IRegistriesPopulator>(), provider.GetRequiredService<IIdentityGenerator>(), provider.GetRequiredService<IStepSerializer>())
                .Handle(new ValidateGraphQuery(graph), CancellationToken.None);
        }

        [Fact]
        public async Task The_reads_are_the_modules_queries_and_a_refused_one_is_nothing()
        {
            var connection = new ConnectionView(Guid.NewGuid(), "Warehouse", "postgresql", "PostgreSQL", "db:5432", new Dictionary<string, string>(), new[] { "password" }, 30, "Connected", null);
            var sales = Guid.NewGuid();
            var mediator = new FakeMediator()
                .On<ListConnectionsQuery>(_ => new[] { connection })
                .On<ListDatasetsQuery>(_ => throw new AccessDeniedException("no datasets:view"))
                .On<GetDatasetQuery>(query => query.Id == sales ? Dataset(sales, "Sales", DataClassification.Confidential, new DatasetColumnSummaryDescriptor("Revenue", Array.Empty<string>(), null, null, 90, 1)) : null)
                .On<ListStepsQuery>(_ => new List<StepCatalogEntry> { new() { Type = "Stomme.WriteLine", DisplayName = "Write line", Category = "Console", Kind = "Action", Inputs = { new StepInputView { Name = "Text", TypeName = "String" } } } });
            var gateway = new MediatorAssistantQueryGateway(mediator, NullLogger<MediatorAssistantQueryGateway>.Instance);

            Assert.Equal(new AssistantConnectionInfo(connection.Id, "Warehouse", "postgresql", "Connected"), Assert.Single(await gateway.ListConnectionsAsync()));
            Assert.Empty(await gateway.ListDatasetsAsync());

            // The Datasets module's masked summary passes through as it is - no samples, no range for a Confidential dataset.
            var summary = (await gateway.GetDataSummaryAsync(sales))!;
            Assert.Equal(("Revenue", 0, (string?)null, 90), (summary.Columns[0].Name, summary.Columns[0].Samples.Count, summary.Columns[0].Min, summary.Columns[0].UniqueCount));
            Assert.Null(await gateway.GetDataSummaryAsync(Guid.NewGuid()));

            var step = Assert.Single(await gateway.ListStepTypesAsync());
            Assert.Equal(("Stomme.WriteLine", "Action", "Text"), (step.Type, step.Kind, step.Inputs.Single().Name));
        }

        [Fact]
        public async Task Help_is_searched_by_term_ranked_by_terms_hit_and_carried_as_text()
        {
            HelpTopicSummary Topic(string anchor, string title, bool published = true) => new(Guid.NewGuid(), anchor, "en", title, published, false, null);
            var create = Topic("pipelines.create", "Create a pipeline");
            var schedule = Topic("pipelines.schedule", "Schedule a pipeline");
            var draft = Topic("pipelines.draft", "A draft", published: false);
            var mediator = new FakeMediator()
                .On<ListPublishedHelpTopicsQuery>(query => query.Query switch
                {
                    "create" => new[] { create, draft },
                    "pipeline" => new[] { create, schedule },
                    _ => Array.Empty<HelpTopicSummary>(),
                })
                .On<ResolveHelpTopicQuery>(query => new HelpTopicView(query.Anchor!, query.Locale!, query.Anchor == create.Anchor ? create.Title : schedule.Title, "<p>Use <b>New</b>.</p>"));
            var gateway = new MediatorAssistantQueryGateway(mediator, NullLogger<MediatorAssistantQueryGateway>.Instance);

            var topics = await gateway.SearchHelpAsync("How do I create a pipeline?", 5);

            Assert.Equal(new[] { "pipelines.create", "pipelines.schedule" }, topics.Select(topic => topic.Anchor));
            Assert.Equal("Use New .", topics[0].Text);
            Assert.Equal(new[] { "create", "pipeline" }, mediator.Sent.OfType<ListPublishedHelpTopicsQuery>().Select(query => query.Query));
            Assert.Single(await gateway.SearchHelpAsync("How do I create a pipeline?", 1));
        }

        [Fact]
        public async Task A_pipeline_draft_becomes_the_editors_graph_and_the_engine_accepts_it()
        {
            var connection = Guid.NewGuid();
            var draft = new AssistantPipelineDraft("Nightly", "reads and decides",
                [
                    new AssistantPipelineStepDraft("Stomme.WriteLine", "Start", Inputs: new Dictionary<string, string> { ["Text"] = "go" }),
                    new AssistantPipelineStepDraft("Stomme.FlowDecision", "Decide", Inputs: new Dictionary<string, string> { ["Condition"] = "true" }),
                    new AssistantPipelineStepDraft("Stomme.WriteLine", "Yes", Inputs: new Dictionary<string, string> { ["Text"] = "yes" }),
                    new AssistantPipelineStepDraft("Stomme.WriteLine", "No", Inputs: new Dictionary<string, string> { ["Text"] = "no" }),
                ],
                [new AssistantPipelineRelationDraft(0, 1), new AssistantPipelineRelationDraft(1, 2, "True"), new AssistantPipelineRelationDraft(1, 3, "False")]);

            Assert.Empty(AssistantPipelineGraph.Check(draft, await CatalogAsync()));
            var graph = AssistantPipelineGraph.ToGraph(draft);
            Assert.Empty(await EngineVerdictAsync(graph));

            Assert.Equal(new[] { "step-1", "step-2", "step-3", "step-4" }, graph.Steps.Select(step => step.Id));
            Assert.Equal(new[] { (0d, 0d), (260d, 0d), (520d, 0d), (520d, 140d) }, graph.Steps.Select(step => (step.Position.X, step.Position.Y)));
            Assert.Equal(("step-2", "True", "step-3"), (graph.Connections[1].SourceId, graph.Connections[1].SourcePort, graph.Connections[1].TargetId));

            // A connection id fills the step's Connection input.
            var withConnection = AssistantPipelineGraph.ToGraph(new AssistantPipelineDraft("x", null, [new AssistantPipelineStepDraft("Stomme.ExtractFromConnection", "Read", connection)], []));
            Assert.Equal(connection.ToString(), withConnection.Steps.Single().Inputs["Connection"]);
        }

        [Fact]
        public async Task What_the_assistant_refuses_before_the_engine_judges_and_what_the_engine_refuses()
        {
            var catalog = (await CatalogAsync()).ToList();
            catalog.Add(new StepCatalogEntry { Type = "Test.Secret", Kind = "Action", Inputs = { new StepInputView { Name = "Password", TypeName = "String", IsSensitive = true } } });

            AssistantPipelineDraft Draft(params AssistantPipelineStepDraft[] steps) => new("p", null, steps, [new AssistantPipelineRelationDraft(0, steps.Length > 1 ? 1 : 0, steps.Length > 1 ? "Maybe" : null)]);

            var errors = AssistantPipelineGraph.Check(Draft(new AssistantPipelineStepDraft("Stomme.NoSuchStep", "x")), catalog).ToList();
            Assert.Contains(errors, error => error.Contains("not a step type"));
            Assert.Contains(errors, error => error.Contains("connected to itself"));

            errors = AssistantPipelineGraph.Check(Draft(
                new AssistantPipelineStepDraft("Stomme.FlowDecision", "d", Inputs: new Dictionary<string, string> { ["Nonsense"] = "1" }),
                new AssistantPipelineStepDraft("Test.Secret", "s", Guid.NewGuid(), new Dictionary<string, string> { ["Password"] = "hunter2" })), catalog).ToList();
            Assert.Contains(errors, error => error.Contains("'Nonsense', which 'Stomme.FlowDecision' does not take"));
            Assert.Contains(errors, error => error.Contains("sensitive input 'Password'"));
            Assert.Contains(errors, error => error.Contains("does not read one"));
            Assert.Contains(errors, error => error.Contains("no outcome port 'Maybe'"));

            // What only the engine can say: an input value its type cannot hold.
            var verdict = await EngineVerdictAsync(AssistantPipelineGraph.ToGraph(new AssistantPipelineDraft("p", null, [new AssistantPipelineStepDraft("Stomme.For", "loop", Inputs: new Dictionary<string, string> { ["Start"] = "one" })], [])));
            Assert.Contains("Int32", Assert.Single(verdict));
            Assert.DoesNotContain("(Parameter", verdict[0]);
        }

        [Fact]
        public async Task Create_pipeline_creates_then_saves_the_graph_and_undoes_the_create_when_the_save_fails()
        {
            var draft = new AssistantPipelineDraft("Nightly", null, [new AssistantPipelineStepDraft("Stomme.WriteLine", "Say", Inputs: new Dictionary<string, string> { ["Text"] = "hi" })], []);
            var catalog = await CatalogAsync();
            PipelineGraph? saved = null;
            var mediator = new FakeMediator()
                .On<ListStepsQuery>(_ => catalog)
                .On<ValidateGraphQuery>(_ => Array.Empty<string>())
                .On<CreatePipelineCommand>(command => new PipelineView { Id = "v1", DefinitionId = "def-1", Name = command.Name })
                .On<SaveGraphCommand>(command =>
                {
                    saved = command.Graph;
                    return new PipelineView { Id = "v1", DefinitionId = command.DefinitionId };
                })
                .On<DeletePipelineCommand>(_ => true);
            var gateway = new MediatorAssistantCommandGateway(mediator, NullLogger<MediatorAssistantCommandGateway>.Instance);

            Assert.Equal("def-1", await gateway.CreatePipelineAsync(draft));
            Assert.Equal(new[] { typeof(ListStepsQuery), typeof(ValidateGraphQuery), typeof(CreatePipelineCommand), typeof(SaveGraphCommand) }, mediator.Sent.Select(request => request.GetType()));
            Assert.Equal("hi", saved!.Steps.Single().Inputs["Text"]);

            mediator.On<SaveGraphCommand>(_ => throw new ArgumentException("the engine refused it"));
            mediator.Sent.Clear();
            Assert.Contains("the engine refused it", (await Assert.ThrowsAsync<AssistantConflictException>(() => gateway.CreatePipelineAsync(draft))).Message);
            Assert.Equal("def-1", Assert.IsType<DeletePipelineCommand>(mediator.Sent.Last()).DefinitionId);

            // An invalid draft creates nothing at all.
            mediator.On<ValidateGraphQuery>(_ => new[] { "no" });
            mediator.Sent.Clear();
            await Assert.ThrowsAsync<AssistantConflictException>(() => gateway.CreatePipelineAsync(draft));
            Assert.DoesNotContain(mediator.Sent, request => request is CreatePipelineCommand);
        }

        [Fact]
        public async Task Create_dashboard_adds_each_widget_and_undoes_the_board_when_a_widget_is_refused()
        {
            var board = Guid.NewGuid();
            var dataset = Guid.NewGuid();
            var draft = new AssistantDashboardDraft("Sales", 12, 40,
            [
                new AssistantWidgetDraft("Chart", "chart", dataset, ["Month", "Revenue"], 0, 0, 6, 4, """{"series":[]}"""),
                new AssistantWidgetDraft("Grid", "Grid", dataset, ["Month"], 6, 0, 6, 4, """{"ignored":true}"""),
            ]);
            var added = new List<AddWidgetCommand>();
            var mediator = new FakeMediator()
                .On<CreateDashboardCommand>(_ => board)
                .On<AddWidgetCommand>(command =>
                {
                    added.Add(command);
                    return Guid.NewGuid();
                })
                .On<DeleteDashboardCommand>(_ => Unit.Value);
            var gateway = new MediatorAssistantCommandGateway(mediator, NullLogger<MediatorAssistantCommandGateway>.Instance);

            Assert.Equal(board, await gateway.CreateDashboardAsync(draft));
            Assert.Equal(new[] { WidgetKind.Chart, WidgetKind.Grid }, added.Select(command => command.Kind));
            Assert.Equal("""{"series":[]}""", added[0].ChartSpecJson);
            Assert.Null(added[1].ChartSpecJson);
            Assert.All(added, command => Assert.Equal(board, command.DashboardId));

            mediator.On<AddWidgetCommand>(command => command.Kind == WidgetKind.Grid ? throw new InvalidOperationException("refused") : Guid.NewGuid());
            mediator.Sent.Clear();
            await Assert.ThrowsAsync<AssistantConflictException>(() => gateway.CreateDashboardAsync(draft));
            Assert.Equal(board, Assert.IsType<DeleteDashboardCommand>(mediator.Sent.Last()).Id);

            // A kind the module does not have is refused before any command.
            mediator.Sent.Clear();
            await Assert.ThrowsAsync<AssistantConflictException>(() => gateway.CreateDashboardAsync(draft with { Widgets = [draft.Widgets[0] with { Kind = "Pie" }] }));
            Assert.Empty(mediator.Sent);

            // A denied key is not a refusal to reword: it propagates as it is, after the undo.
            mediator.On<AddWidgetCommand>(_ => throw new AccessDeniedException("no dashboards:manage"));
            mediator.Sent.Clear();
            await Assert.ThrowsAsync<AccessDeniedException>(() => gateway.CreateDashboardAsync(draft));
            Assert.IsType<DeleteDashboardCommand>(mediator.Sent.Last());
        }
    }
}
