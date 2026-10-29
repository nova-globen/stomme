#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Find in every pipeline: where a text is used - a step's name, comment and inputs as written (values, expressions,
    /// scripts, conditions, cases), a note, a variable - with a snippet, case ignored; a sensitive input and a kept test value
    /// never read; across the site's pipelines in name order, archived ones left out, each place with its folder and step.
    /// </summary>
    public sealed class PipelineFindTests : IAsyncLifetime
    {
        private ServiceProvider _engine = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _engine = services.BuildServiceProvider();
            using var scope = _engine.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _engine.DisposeAsync();

        private static readonly StepCatalogEntry[] Catalog =
        {
            new() { Type = "Test.Fetch", DisplayName = "Fetch", Inputs = { new StepInputView { Name = "Url", DisplayName = "Address", TypeName = "String" }, new StepInputView { Name = "Token", TypeName = "String", IsSensitive = true } } },
        };

        [Fact]
        public void Every_place_a_text_is_used_is_found_and_a_secret_never_read()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep
                    {
                        Id = "fetch", Type = "Test.Fetch", Name = "Get the ORDERS", Comment = "reads the orders table",
                        Inputs = { ["Url"] = "https://shop.example.com/api/orders?page=1", ["Token"] = "orders-secret" },
                        InputExpressions = { ["Body"] = "{{ $vars.ordersBase }}" },
                        InputScripts = { ["Extra"] = "return input.orders.length;" },
                    },
                    new PipelineGraphStep { Id = "route", Type = "Stomme.FlowSwitch", Cases = { new PipelineGraphCase { Label = "Big orders", Condition = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ total }}", Operator = PipelineConditions.EqualsOperator, Right = "1" } } } } } },
                    new PipelineGraphStep { Id = "unnamed", Type = "Test.Fetch", Inputs = { ["Url"] = "nothing here" } },
                },
                Notes = { new PipelineGraphNote { Id = "n", Text = "Orders come in at night.\nThey are checked first." } },
                Variables = { new PipelineGraphVariable { Id = "v", Name = "orders", Value = "[]", TestValue = "orders pinned" } },
            };

            var found = PipelineContentSearch.Find(graph, "orders", Catalog);

            Assert.Equal(new (string?, string, string)[]
            {
                ("fetch", "step 'Get the ORDERS', its name", "Get the ORDERS"),
                ("fetch", "step 'Get the ORDERS', its comment", "reads the orders table"),
                ("fetch", "step 'Get the ORDERS', input 'Address'", "https://shop.example.com/api/orders?page=1"),
                ("fetch", "step 'Get the ORDERS', the expression of input 'Body'", "{{ $vars.ordersBase }}"),
                ("fetch", "step 'Get the ORDERS', the JavaScript of input 'Extra'", "return input.orders.length;"),
                ("route", "step 'Stomme.FlowSwitch', case 'Big orders'", "Big orders {{ total }} is equal to 1"),
                (null, "a note", "Orders come in at night. They are ch…"), // 30 characters past it, on one line
                (null, "variable 'orders'", "orders"),
            }, found.ToArray());
            Assert.DoesNotContain(found, place => place.Snippet.Contains("secret", StringComparison.Ordinal) || place.Snippet.Contains("pinned", StringComparison.Ordinal));

            // A long value is cut around what was found; a text too short is not looked for.
            var cut = PipelineContentSearch.Find(new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Test.Fetch", Inputs = { ["Url"] = new string('x', 60) + "needle" + new string('y', 60) } } } }, "NEEDLE", Catalog).Single();
            Assert.Equal("…" + new string('x', 30) + "needle" + new string('y', 30) + "…", cut.Snippet);
            Assert.Empty(PipelineContentSearch.Find(graph, "o", Catalog));
            Assert.Empty(PipelineContentSearch.Find(graph, "  ", Catalog));
        }

        [Fact]
        public async Task The_sites_pipelines_are_searched_in_name_order_with_their_folders_the_archived_left_out()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var keyValues = services.GetRequiredService<IKeyValueStore>();
            var tenant = services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>().TenantId;
            async Task<string> MakeAsync(string name, string text)
            {
                var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Inputs = { ["Text"] = text } } } };
                var definition = await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(
                    PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>())));
                definition.Name = name;
                await publisher.SaveDraftAsync(definition);
                return definition.DefinitionId;
            }

            var zebra = await MakeAsync("Zebra report", "uses the orders table");
            var alpha = await MakeAsync("Alpha sync", "copies ORDERS nightly");
            var other = await MakeAsync("Unrelated", "says hello");
            var setAside = await MakeAsync("Set aside", "orders, once");
            await PipelineFolderStore.SaveAsync(keyValues, tenant, zebra, "Reports", CancellationToken.None);
            await new ArchivePipelineCommandHandler(services.GetRequiredService<IPipelineDefinitionStore>(), publisher, keyValues,
                    services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>(), new Someone(), new FixedClock())
                .Handle(new ArchivePipelineCommand(setAside), CancellationToken.None);

            var found = await new FindInPipelinesQueryHandler(services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IStepRegistry>(),
                    services.GetRequiredService<IRegistriesPopulator>(), keyValues, services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>())
                .Handle(new FindInPipelinesQuery("orders"), CancellationToken.None);

            Assert.Equal(new[] { ("Alpha sync", "", "say"), ("Zebra report", "Reports", "say") }, found.Select(place => (place.PipelineName!, place.Folder, place.StepId!)).ToArray());
            Assert.Equal("step 'Say', input 'Text'", found[0].Where);
            Assert.DoesNotContain(found, place => place.DefinitionId == other || place.DefinitionId == setAside);
            Assert.Empty(await new FindInPipelinesQueryHandler(services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IStepRegistry>(),
                    services.GetRequiredService<IRegistriesPopulator>(), keyValues, services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>())
                .Handle(new FindInPipelinesQuery("o"), CancellationToken.None));
        }

        // The map: Execute pipeline calls, Dispatch starts, a failure starts what watches it (every pipeline: a mark, no
        // line), an agent uses the tools it names (or every one); a step switched off, a pipeline named by an expression, one
        // the map does not hold and a pipeline calling itself make no line.
        [Fact]
        public void The_map_says_which_pipeline_runs_which()
        {
            PipelineGraphStep Step(string type, params (string Input, string Value)[] inputs)
            {
                var step = new PipelineGraphStep { Id = Guid.NewGuid().ToString("N"), Type = type };
                foreach (var (input, value) in inputs)
                {
                    step.Inputs[input] = value;
                }

                return step;
            }

            PipelineGraph Of(params PipelineGraphStep[] steps) => new() { Steps = steps.ToList() };
            var off = Step("Stomme.ExecutePipeline", ("PipelineDefinitionId", "c"));
            off.Disabled = true;
            var byExpression = Step("Stomme.ExecutePipeline");
            byExpression.InputExpressions["PipelineDefinitionId"] = "{{ which }}";
            var map = PipelineLinks.Map(new (string, string?, string?, bool, PipelineGraph?)[]
            {
                ("a", "Orders", "", true, Of(Step("Stomme.ExecutePipeline", ("PipelineDefinitionId", "b")), Step("Stomme.DispatchPipeline", ("PipelineDefinitionId", "c")), off, byExpression,
                    Step("Stomme.ExecutePipeline", ("PipelineDefinitionId", "elsewhere")), Step("Stomme.ExecutePipeline", ("PipelineDefinitionId", "a")))),
                ("b", "Bill", "Money", true, Of()),
                ("c", "Ship", "", false, Of()),
                ("d", "On bill failure", "", true, Of(Step(PipelineFailure.StepType, ("Pipeline", "b")))),
                ("e", "On any failure", "", true, Of(Step(PipelineFailure.StepType))),
                ("t", "Lookup tool", "", true, Of(Step(PipelineLinks.AgentToolType, ("ToolName", "lookup")))),
                ("g", "Agent naming it", "", true, Of(Step(PipelineLinks.AgentType, ("Tools", "lookup\nmissing")))),
                ("h", "Agent with all", "", true, Of(Step(PipelineLinks.AgentType))),
                ("x", "Unparsed", "", true, null),
            });

            Assert.Equal(new[]
            {
                ("g", "t", PipelineLinks.AsTool), ("h", "t", PipelineLinks.AsTool), ("b", "d", PipelineLinks.OnFailure),
                ("a", "b", PipelineLinks.Calls), ("a", "c", PipelineLinks.Starts),
            }, map.Links.Select(link => (link.From, link.To, link.Kind)).OrderBy(link => link.From == "a" ? 1 : 0).ToArray());
            Assert.True(map.Nodes.Single(node => node.DefinitionId == "e").WatchesEvery);
            Assert.True(map.Nodes.Single(node => node.DefinitionId == "t").IsTool);
            Assert.Equal(("Money", true, false), (map.Nodes.Single(node => node.DefinitionId == "b").Folder, map.Nodes.Single(node => node.DefinitionId == "b").IsPublished, map.Nodes.Single(node => node.DefinitionId == "c").IsPublished));
            Assert.Equal(9, map.Nodes.Count);
        }

        // What stops working when pipelines stop running: a call or a start into one of them, a tool an agent uses, the
        // failures one handles, a handler of every failure - never a link among the ones leaving, nor one out of them.
        [Fact]
        public void What_stops_working_is_said_before_a_pipeline_stops_running()
        {
            PipelineGraphStep Step(string type, params (string Input, string Value)[] inputs)
            {
                var step = new PipelineGraphStep { Id = Guid.NewGuid().ToString("N"), Type = type };
                foreach (var (input, value) in inputs)
                {
                    step.Inputs[input] = value;
                }

                return step;
            }

            PipelineGraph Of(params PipelineGraphStep[] steps) => new() { Steps = steps.ToList() };
            var map = PipelineLinks.Map(new (string, string?, string?, bool, PipelineGraph?)[]
            {
                ("a", "Orders", "", true, Of(Step("Stomme.ExecutePipeline", ("PipelineDefinitionId", "b")), Step("Stomme.DispatchPipeline", ("PipelineDefinitionId", "c")))),
                ("b", "Bill", "", true, Of()),
                ("c", null, "", true, Of()),
                ("d", "On bill failure", "", true, Of(Step(PipelineFailure.StepType, ("Pipeline", "b")))),
                ("e", "On any failure", "", true, Of(Step(PipelineFailure.StepType))),
                ("t", "Lookup tool", "", true, Of(Step(PipelineLinks.AgentToolType, ("ToolName", "lookup")))),
                ("g", "Agent", "", true, Of(Step(PipelineLinks.AgentType, ("Tools", "lookup")))),
            });

            Assert.Equal(new[] { "'Orders' calls 'Bill' - that call will fail." }, PipelineLinks.LeftBehind(map, new[] { "b" }));
            Assert.Equal(new[] { "'Orders' starts 'c' - that start will fail." }, PipelineLinks.LeftBehind(map, new[] { "c" }));
            Assert.Equal(new[] { "'On bill failure' handles the failures of 'Bill' - they will go unhandled." }, PipelineLinks.LeftBehind(map, new[] { "d" }));
            Assert.Equal(new[] { "'Agent' may use 'Lookup tool' as an AI tool - it will not be offered." }, PipelineLinks.LeftBehind(map, new[] { "t" }));
            Assert.Equal(new[] { "'On any failure' handles the failures of every pipeline - not while it does not run." }, PipelineLinks.LeftBehind(map, new[] { "e" }));
            Assert.Equal(new[] { "'Orders' starts 'c' - that start will fail.", "'Orders' calls 'Bill' - that call will fail." }, PipelineLinks.LeftBehind(map, new[] { "b", "c" })); // the map's order: a pipeline with no name first

            // Leaving together, nothing that stays breaks; a pipeline that only runs others leaves nothing behind.
            Assert.Empty(PipelineLinks.LeftBehind(map, new[] { "a", "b", "c" }));
            Assert.Empty(PipelineLinks.LeftBehind(map, new[] { "a" }));
            Assert.Empty(PipelineLinks.LeftBehind(map, new[] { "g" }));
            Assert.Empty(PipelineLinks.LeftBehind(map, Array.Empty<string>()));
            Assert.Empty(PipelineLinks.LeftBehind(null, new[] { "b" }));
        }

        // The map reads the version that runs too: a call only the published version makes is drawn beside a start only
        // the draft makes - so archiving the called pipeline is said to break the call that runs today.
        [Fact]
        public async Task The_map_holds_the_published_versions_links_and_the_drafts()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var registry = services.GetRequiredService<IStepRegistry>();
            var identities = services.GetRequiredService<IIdentityGenerator>();
            async Task<string> NewAsync(string name, PipelineGraph graph)
            {
                var definition = await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(PipelineGraphCodec.ToBody(graph, null, registry, identities)));
                definition.Name = name;
                await publisher.SaveDraftAsync(definition);
                var published = await publisher.PublishAsync(definition);
                Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(error => error.Message)));
                return definition.DefinitionId;
            }

            PipelineGraph Saying() => new() { Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Inputs = { ["Text"] = "done" } } } };
            PipelineGraph Calling(string type, string id) => new() { Steps = { new PipelineGraphStep { Id = "call", Type = type, Name = "Call", Inputs = { ["PipelineDefinitionId"] = id } } } };
            var billing = await NewAsync("Billing", Saying());
            var shipping = await NewAsync("Shipping", Saying());
            var orders = await NewAsync("Orders", Calling("Stomme.ExecutePipeline", billing));
            var draft = (await publisher.GetDraftAsync(orders, Stomme.Engine.Common.Models.VersionOptions.Latest))!;
            draft.StringData = PipelineGraphCodec.ToBody(Calling("Stomme.DispatchPipeline", shipping), draft.StringData, registry, identities);
            await publisher.SaveDraftAsync(draft);

            var map = await new GetPipelineMapQueryHandler(services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IKeyValueStore>(),
                    services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>())
                .Handle(new GetPipelineMapQuery(), CancellationToken.None);

            Assert.Equal(new[] { (orders, billing, PipelineLinks.Calls), (orders, shipping, PipelineLinks.Starts) }, map.Links.Select(link => (link.From, link.To, link.Kind)).ToArray());
            Assert.Equal(new[] { "'Orders' calls 'Billing' - that call will fail." }, PipelineLinks.LeftBehind(map, new[] { billing }));
        }

        private sealed class FixedClock : Stomme.Engine.Common.ISystemClock
        {
            public DateTimeOffset UtcNow => new(2026, 10, 9, 2, 0, 0, TimeSpan.Zero);
        }

        private sealed class Someone : Stomme.Security.Abstractions.ICurrentUser
        {
            public int? UserId => 7;
            public string? UserName => "ada";
            public bool IsAuthenticated => true;
            public IReadOnlySet<string> Roles => new HashSet<string>();
            public int SiteId => 1;
        }
    }
}
