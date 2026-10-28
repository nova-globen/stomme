#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Stores;
using Stomme.Engine.Pipelines.State;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Pipelines;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 increment 2 - a run's step executions as the editor shows them: in the order they started, without the engine's
    /// own wrappers; states and faults for anyone who may view, inputs and outputs for an AUTHOR only, an input the step marks
    /// as able to hold a secret hidden even then, and a long value cut and flagged.
    /// </summary>
    public sealed class PipelineRunStepsTests : IAsyncLifetime
    {
        private ServiceProvider _engine = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            _engine = services.BuildServiceProvider();
            using var scope = _engine.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _engine.DisposeAsync();

        private static readonly DateTimeOffset Start = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);

        private static StepExecutionRecord Record(string stepId, string type, int second, StepStatus status, Dictionary<string, object?>? state = null, Dictionary<string, object?>? outputs = null, Exception? fault = null) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            PipelineInstanceId = "run-1",
            StepId = stepId,
            StepNodeId = "node-" + stepId,
            StepType = type,
            StepTypeVersion = 1,
            StepName = stepId,
            StartedAt = Start.AddSeconds(second),
            CompletedAt = status == StepStatus.Running ? null : Start.AddSeconds(second + 1),
            Status = status,
            StepState = state,
            Outputs = outputs,
            Exception = fault is null ? null : ExceptionState.FromException(fault),
        };

        private async Task<IReadOnlyList<Stomme.Models.Pipelines.PipelineStepRunView>> ListAsync(bool author)
        {
            var store = new MemoryStepExecutionStore(new MemoryStore<StepExecutionRecord>());
            await store.SaveManyAsync(new[]
            {
                Record("call", "Stomme.SendHttpRequest", 2, StepStatus.Completed,
                    new() { ["Url"] = "https://example.test/orders", ["Authorization"] = "Bearer top-secret" },
                    // As the engine's store hands it back: a string-KIND JsonElement holding JSON text.
                    new() { ["Result"] = System.Text.Json.JsonDocument.Parse("\"{\\\"ok\\\":true}\"").RootElement.Clone() }),
                Record("root", "Stomme.Flowchart", 0, StepStatus.Completed),
                Record("pipeline", "Stomme.Pipeline", 0, StepStatus.Completed),
                Record("say", "Stomme.WriteLine", 3, StepStatus.Faulted, new() { ["Text"] = new string('x', 40_000) }, fault: new InvalidOperationException("the disk is full")),
                Record("wait", "Stomme.Delay", 1, StepStatus.Running),
            });

            using var scope = _engine.CreateScope();
            var handler = new ListRunStepsQueryHandler(store, scope.ServiceProvider.GetRequiredService<IStepRegistry>(), new Guard(author));
            return await handler.Handle(new ListRunStepsQuery("run-1"), CancellationToken.None);
        }

        [Fact]
        public async Task An_author_sees_each_step_in_start_order_with_its_data_and_a_secret_input_hidden()
        {
            var steps = await ListAsync(author: true);

            // In start order, the engine's pipeline and flowchart wrappers left out.
            Assert.Equal(new[] { "wait", "call", "say" }, steps.Select(step => step.StepId));
            Assert.Equal(new[] { "Running", "Completed", "Faulted" }, steps.Select(step => step.Status));
            Assert.Null(steps[0].CompletedAtUtc);
            Assert.Equal(Start.AddSeconds(3).UtcDateTime, steps[2].StartedAtUtc);

            var call = steps[1];
            Assert.Contains("https://example.test/orders", call.InputsJson);
            Assert.DoesNotContain("top-secret", call.InputsJson);
            Assert.Contains(ListRunStepsQueryHandler.Hidden, call.InputsJson);
            // A value that is JSON text - how the data steps pass records - is shown as the JSON it is, quotes unescaped.
            Assert.Contains("\"ok\": true", call.OutputsJson);
            Assert.DoesNotContain("\\u0022", call.OutputsJson);

            var say = steps[2];
            Assert.Equal("the disk is full", say.Error);
            Assert.True(say.Truncated);
            Assert.True(say.InputsJson!.Length <= ListRunStepsQueryHandler.MaxValueLength + 2);
        }

        // BL-229: a connection step's parameter lines that name a secret - a password, a token, a key, an Authorization header -
        // are shown hidden to an author too, every other line as it was (found driving WordPress's user Create, whose password
        // a run's data listed).
        [Fact]
        public async Task A_connection_steps_secret_parameter_lines_are_hidden_from_an_author_too()
        {
            var store = new MemoryStepExecutionStore(new MemoryStore<StepExecutionRecord>());
            await store.SaveManyAsync(new[]
            {
                Record("user", "Stomme.ExtractFromConnection", 1, StepStatus.Completed, new()
                {
                    ["Operation"] = "create_user",
                    ["Query"] = "SELECT id FROM users WHERE token = @token",
                    ["Parameters"] = "username = grace\r\npassword = correct horse\nclient_secret = c-123\nrefresh-token = r-456\nX-Api-Key = k-789\n"
                        + "headers = Authorization: Bearer b-012\nheaders = X-Trace: 42\nheader = Cookie: session=s-345\ntitle = A token of thanks\nkey = OPS-1",
                }),
                Record("plain", "Stomme.ExtractFromConnection", 2, StepStatus.Completed, new() { ["Parameters"] = "board = 1234567890\nmax = 50" }),
            });
            using var scope = _engine.CreateScope();
            var steps = await new ListRunStepsQueryHandler(store, scope.ServiceProvider.GetRequiredService<IStepRegistry>(), new Guard(true)).Handle(new ListRunStepsQuery("run-1"), CancellationToken.None);

            var inputs = System.Text.Json.JsonDocument.Parse(steps[0].InputsJson!).RootElement.GetProperty("Parameters").GetString();
            Assert.Equal("username = grace\r\npassword = (hidden - it can hold a secret)\nclient_secret = (hidden - it can hold a secret)\nrefresh-token = (hidden - it can hold a secret)\n"
                + "X-Api-Key = (hidden - it can hold a secret)\nheaders = Authorization: (hidden - it can hold a secret)\nheaders = X-Trace: 42\nheader = Cookie: (hidden - it can hold a secret)\n"
                + "title = A token of thanks\nkey = OPS-1", inputs); // a value's own words never hide it; Jira's key is no secret
            Assert.Contains("create_user", steps[0].InputsJson);
            Assert.Equal("SELECT id FROM users WHERE token = @token", System.Text.Json.JsonDocument.Parse(steps[0].InputsJson!).RootElement.GetProperty("Query").GetString()); // the Parameters lines alone
            Assert.Equal("board = 1234567890\nmax = 50", System.Text.Json.JsonDocument.Parse(steps[1].InputsJson!).RootElement.GetProperty("Parameters").GetString());
        }

        [Fact]
        public async Task A_viewer_sees_the_states_and_faults_and_no_value()
        {
            var steps = await ListAsync(author: false);

            Assert.Equal(new[] { "wait", "call", "say" }, steps.Select(step => step.StepId));
            Assert.Equal("the disk is full", steps[2].Error);
            Assert.All(steps, step => Assert.Null(step.InputsJson));
            Assert.All(steps, step => Assert.Null(step.OutputsJson));
        }

        // BL-229: a step whose data its pipeline's setting emptied is said to be so - to a viewer as to an author - with nothing
        // left to show; one kept is not.
        [Fact]
        public async Task A_step_emptied_by_its_pipelines_setting_is_said_not_kept()
        {
            var store = new MemoryStepExecutionStore(new MemoryStore<StepExecutionRecord>());
            var emptied = Record("say", "Stomme.WriteLine", 1, StepStatus.Completed, new() { ["Text"] = "card 4111" }, new() { ["Result"] = "card 4111" });
            Assert.True(Stomme.Modules.Pipelines.Triggers.PipelineRunSettingsTask.Empty(emptied));
            await store.SaveManyAsync(new[] { emptied, Record("call", "Stomme.SendHttpRequest", 2, StepStatus.Completed, new() { ["Url"] = "https://example.test" }) });

            using var scope = _engine.CreateScope();
            foreach (var author in new[] { true, false })
            {
                var steps = await new ListRunStepsQueryHandler(store, scope.ServiceProvider.GetRequiredService<IStepRegistry>(), new Guard(author))
                    .Handle(new ListRunStepsQuery("run-1"), CancellationToken.None);
                Assert.Equal(new[] { ("say", true), ("call", false) }, steps.Select(step => (step.StepId, step.DataNotKept)));
                Assert.Equal(((string?)null, (string?)null), (steps[0].InputsJson, steps[0].OutputsJson));
            }
        }

        private sealed class Guard(bool author) : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) =>
                Task.FromResult(permission == PipelinesPermissions.Manage ? author : true);

            public Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }
    }
}
