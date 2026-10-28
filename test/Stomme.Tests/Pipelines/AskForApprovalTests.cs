#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Pipelines;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - Ask for approval, n8n's "send and wait for approval", through the engine as the host composes it: a run that
    /// reaches the step is STORED, waiting, its bookmark carrying the question; it is listed for the site (the details for those
    /// who may decide only); a person's decision resumes it down Approved or Rejected, with who decided and their comment as the
    /// step's result and a line of the run's log - once, a second decision finding it decided. Given a time to decide, a run
    /// nobody decided goes on down Expired at the task's tick, and a decision after that time is refused.
    /// </summary>
    public sealed class AskForApprovalTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        private readonly PipelineTriggerOptions _options = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(_options));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton<ISystemClock>(_clock);
            services.AddScoped<DueWaitsTask>();
            // What a notice needs: the run's site, the dispatcher, who holds a role, and where the module is placed.
            services.AddScoped(_ => new Stomme.Shared.SiteState { Alias = new Stomme.Models.Alias { AliasId = 1, TenantId = 1, SiteId = 1, Name = "example.test", Protocol = "https://" } });
            services.AddSingleton<Stomme.Notifications.INotificationDispatcher>(_dispatcher);
            services.AddSingleton<Stomme.Repository.IUserRoleRepository>(new Roles());
            services.AddSingleton<Stomme.Repository.IPageModuleRepository>(new Placements());
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
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

        private readonly Dispatcher _dispatcher = new();

        /// <summary>The site's notifications, as the module's dispatcher receives them; refuses when a test says.</summary>
        private sealed class Dispatcher : Stomme.Notifications.INotificationDispatcher
        {
            public List<Stomme.Models.Notifications.NotificationDispatchRequest> Sent { get; } = new();

            public string? Refusal { get; set; }

            public Task<Guid> DispatchAsync(Stomme.Models.Notifications.NotificationDispatchRequest request, CancellationToken ct = default)
            {
                if (Refusal is not null)
                {
                    throw new InvalidOperationException(Refusal);
                }

                Sent.Add(request);
                return Task.FromResult(Guid.NewGuid());
            }
        }

        /// <summary>
        /// The role Approvers on site 1: user 11 now, user 12 whose membership ended, user 13 deleted, user 14 not yet. As on a
        /// fresh install, no one holds Administrators; the host, user 1, holds Host Users.
        /// </summary>
        private sealed class Roles : Stomme.Repository.IUserRoleRepository
        {
            public Task<IEnumerable<Stomme.Models.UserRole>> GetUserRolesAsync(string roleName, int siteId) => Task.FromResult<IEnumerable<Stomme.Models.UserRole>>(
                roleName == RoleNames.Host
                    ? new[] { new Stomme.Models.UserRole { UserId = 1, User = new Stomme.Models.User { UserId = 1 } } }
                    : roleName == "Approvers" && siteId == 1
                    ? new[]
                    {
                        new Stomme.Models.UserRole { UserId = 11, User = new Stomme.Models.User { UserId = 11 } },
                        new Stomme.Models.UserRole { UserId = 12, ExpiryDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), User = new Stomme.Models.User { UserId = 12 } },
                        new Stomme.Models.UserRole { UserId = 13, User = new Stomme.Models.User { UserId = 13, IsDeleted = true } },
                        new Stomme.Models.UserRole { UserId = 14, EffectiveDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), User = new Stomme.Models.User { UserId = 14 } },
                    }
                    : Array.Empty<Stomme.Models.UserRole>());

            public Task<IEnumerable<Stomme.Models.UserRole>> GetUserRolesAsync(int siteId) => throw new NotSupportedException();
            public Task<IEnumerable<Stomme.Models.UserRole>> GetUserRolesAsync(int userId, int siteId) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> AddUserRoleAsync(Stomme.Models.UserRole userRole) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> UpdateUserRoleAsync(Stomme.Models.UserRole userRole) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> GetUserRoleAsync(int userRoleId) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> GetUserRoleAsync(int userRoleId, bool tracking) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> GetUserRoleAsync(int userId, int roleId) => throw new NotSupportedException();
            public Task<Stomme.Models.UserRole> GetUserRoleAsync(int userId, int roleId, bool tracking) => throw new NotSupportedException();
            public Task DeleteUserRoleAsync(int userRoleId) => throw new NotSupportedException();
            public Task DeleteUserRolesAsync(int userId) => throw new NotSupportedException();
        }

        /// <summary>The Pipelines module on site 1's admin page, as instance 59 - and a deleted placement elsewhere.</summary>
        private sealed class Placements : Stomme.Repository.IPageModuleRepository
        {
            public Task<IEnumerable<Stomme.Models.PageModule>> GetPageModulesAsync(int siteId)
            {
                var definition = typeof(Stomme.Modules.Pipelines.Index).ToModuleDefinitionName();
                return Task.FromResult<IEnumerable<Stomme.Models.PageModule>>(new[]
                {
                    new Stomme.Models.PageModule { ModuleId = 12, IsDeleted = true, Page = new Stomme.Models.Page { Path = "admin/old" }, Module = new Stomme.Models.Module { ModuleDefinitionName = definition } },
                    new Stomme.Models.PageModule { ModuleId = 59, Page = new Stomme.Models.Page { Path = "admin/pipelines" }, Module = new Stomme.Models.Module { ModuleDefinitionName = definition } },
                    new Stomme.Models.PageModule { ModuleId = 3, Page = new Stomme.Models.Page { Path = "home" }, Module = new Stomme.Models.Module { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client" } },
                });
            }

            public Task<Stomme.Models.PageModule> AddPageModuleAsync(Stomme.Models.PageModule pageModule) => throw new NotSupportedException();
            public Task<Stomme.Models.PageModule> UpdatePageModuleAsync(Stomme.Models.PageModule pageModule) => throw new NotSupportedException();
            public Task<Stomme.Models.PageModule> GetPageModuleAsync(int pageModuleId) => throw new NotSupportedException();
            public Task<Stomme.Models.PageModule> GetPageModuleAsync(int pageModuleId, bool tracking) => throw new NotSupportedException();
            public Task<Stomme.Models.PageModule> GetPageModuleAsync(int pageId, int moduleId) => throw new NotSupportedException();
            public Task DeletePageModuleAsync(int pageModuleId) => throw new NotSupportedException();
        }

        /// <summary>A caller holding the given keys and no others.</summary>
        private sealed class Guard(params string[] keys) : Stomme.Security.Abstractions.IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(keys.Contains(permission));

            public Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        private sealed class Person(string? name) : Stomme.Security.Abstractions.ICurrentUser
        {
            public int? UserId => 7;

            public string? UserName => name;

            public bool IsAuthenticated => true;

            public IReadOnlySet<string> Roles { get; } = new HashSet<string>();

            public int SiteId => 1;
        }

        // A webhook starts it; the step asks; each outcome writes a line of its own.
        private async Task<string> PublishAsync(IServiceProvider services, string hook, AskForApproval ask)
        {
            var trigger = new Webhook { WebhookName = new(hook), Id = "trigger" };
            trigger.SetCanStartPipeline(true);
            ask.Id = "ask";
            ask.Name = "Manager OK";
            var approved = new WriteLine("approved " + hook) { Id = "approved" };
            var rejected = new WriteLine("rejected " + hook) { Id = "rejected" };
            var expired = new WriteLine("expired " + hook) { Id = "expired" };
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            // A fixed flowchart id, as the codec writes one: a run is resumed by node id (WaitStepTests).
            var definition = await publisher.NewAsync(new Flowchart
            {
                Id = "flow",
                Steps = { trigger, ask, approved, rejected, expired },
                Connections =
                {
                    new Connection(new Endpoint(trigger), new Endpoint(ask)),
                    new Connection(new Endpoint(ask, PipelineApprovals.Approved), new Endpoint(approved)),
                    new Connection(new Endpoint(ask, PipelineApprovals.Rejected), new Endpoint(rejected)),
                    new Connection(new Endpoint(ask, PipelineApprovals.Expired), new Endpoint(expired)),
                },
            });
            definition.Name = "Approves " + hook;
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
            return definition.DefinitionId;
        }

        private static async Task<PipelineInstance> RunOf(IServiceProvider services, string definitionId) =>
            Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }));

        private static Task<IReadOnlyList<PipelineApprovalView>> ListAsync(IServiceProvider services, string? runId, params string[] keys) =>
            new ListApprovalsQueryHandler(services.GetRequiredService<IBookmarkStore>(), services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineDefinitionStore>(), new Guard(keys))
                .Handle(new ListApprovalsQuery(runId), CancellationToken.None);

        private Task<PipelineApprovalDecided?> DecideAsync(IServiceProvider services, string id, bool approve, string? comment, string? by = "alice") =>
            new DecideApprovalCommandHandler(services.GetRequiredService<IBookmarkStore>(), services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineResumer>(), new Person(by), _clock, NullLogger<DecideApprovalCommandHandler>.Instance)
                .Handle(new DecideApprovalCommand(id, approve, comment), CancellationToken.None);

        private static async Task<IReadOnlyList<PipelineStepRunView>> StepsOf(IServiceProvider services, string runId) =>
            await new ListRunStepsQueryHandler(services.GetRequiredService<IStepExecutionStore>(), services.GetRequiredService<IStepRegistry>(), new Guard(PipelinesPermissions.Manage))
                .Handle(new ListRunStepsQuery(runId), CancellationToken.None);

        [Fact]
        public async Task A_run_waits_for_a_decision_and_goes_on_down_the_outcome_chosen_once()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "pay", new AskForApproval { Question = new("Pay invoice 42?"), Details = new("Amount: 1200 EUR") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("pay"));

            // Stored, waiting; the step parked on its bookmark.
            var run = await RunOf(services, definitionId);
            Assert.Equal(PipelineStatus.Running, run.Status);
            Assert.DoesNotContain("pay", _console.ToString());
            Assert.True(Assert.Single(await StepsOf(services, run.Id), step => step.StepId == "ask").Waiting);

            // Listed for the site and for its run - the details for those who may decide (or author) only.
            var approval = Assert.Single(await ListAsync(services, null, PipelinesPermissions.View, PipelinesPermissions.Approve));
            Assert.Equal(run.Id, approval.RunId);
            Assert.Equal(definitionId, approval.DefinitionId);
            Assert.Equal("Approves pay", approval.PipelineName);
            Assert.Equal(("ask", "Manager OK"), (approval.StepId, approval.StepName));
            Assert.Equal("Pay invoice 42?", approval.Question);
            Assert.Equal("Amount: 1200 EUR", approval.Details);
            Assert.Equal(_clock.UtcNow.UtcDateTime, approval.RequestedOnUtc);
            Assert.Null(approval.ExpiresOnUtc);
            Assert.Null(Assert.Single(await ListAsync(services, null, PipelinesPermissions.View)).Details);
            Assert.Equal("Amount: 1200 EUR", Assert.Single(await ListAsync(services, run.Id, PipelinesPermissions.Manage)).Details);
            Assert.Empty(await ListAsync(services, "another-run", PipelinesPermissions.View));

            // Approved, with a comment: the run goes on down Approved, and only there.
            var decided = await DecideAsync(services, approval.Id, approve: true, comment: "  Within budget ");
            Assert.NotNull(decided);
            Assert.Equal((run.Id, PipelineApprovals.Approved), (decided!.RunId, decided.Outcome));
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, definitionId)).Status);
            Assert.Contains("approved pay", _console.ToString());
            Assert.DoesNotContain("rejected pay", _console.ToString());

            // Who decided, and what they said: the step's result, and a line of the run's log.
            var asked = Assert.Single(await StepsOf(services, run.Id), step => step.StepId == "ask");
            Assert.False(asked.Waiting);
            Assert.Contains("\"decision\": \"Approved\"", asked.OutputsJson);
            Assert.Contains("\"by\": \"alice\"", asked.OutputsJson);
            Assert.Contains("\"comment\": \"Within budget\"", asked.OutputsJson);
            var log = await new GetRunLogQueryHandler(services.GetRequiredService<IPipelineExecutionLogStore>()).Handle(new GetRunLogQuery(run.Id), CancellationToken.None);
            Assert.Contains(log, line => line.EventName == "Awaiting approval" && line.Message == "Pay invoice 42?");
            Assert.Contains(log, line => line.EventName == PipelineApprovals.Approved && line.Message == "Approved by alice: Within budget");

            // Once: decided already, it is gone from the list and a second decision finds nothing.
            Assert.Empty(await ListAsync(services, null, PipelinesPermissions.View));
            Assert.Null(await DecideAsync(services, approval.Id, approve: false, comment: null, by: "bob"));
            Assert.Single(_console.ToString().Split("approved pay").Skip(1));
        }

        [Fact]
        public async Task A_rejection_goes_down_Rejected_and_a_second_approval_never_reads_the_first_ones_decision()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;

            // Two approvals in a row: the first approved with a comment, the second rejected without one.
            var trigger = new Webhook { WebhookName = new("twice"), Id = "trigger" };
            trigger.SetCanStartPipeline(true);
            var first = new AskForApproval { Id = "first", Name = "First", Question = new("First?") };
            var second = new AskForApproval { Id = "second", Name = "Second", Question = new("Second?") };
            var rejected = new WriteLine("rejected twice") { Id = "rejected" };
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart
            {
                Id = "flow",
                Steps = { trigger, first, second, rejected },
                Connections =
                {
                    new Connection(new Endpoint(trigger), new Endpoint(first)),
                    new Connection(new Endpoint(first, PipelineApprovals.Approved), new Endpoint(second)),
                    new Connection(new Endpoint(second, PipelineApprovals.Rejected), new Endpoint(rejected)),
                },
            });
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("twice"));

            var one = Assert.Single(await ListAsync(services, null, PipelinesPermissions.View));
            Assert.Equal("First?", one.Question);
            Assert.NotNull(await DecideAsync(services, one.Id, approve: true, comment: "go ahead"));

            var two = Assert.Single(await ListAsync(services, null, PipelinesPermissions.View));
            Assert.Equal("Second?", two.Question);
            Assert.Equal(PipelineApprovals.Rejected, (await DecideAsync(services, two.Id, approve: false, comment: null, by: "bob"))!.Outcome);

            var run = await RunOf(services, definition.DefinitionId);
            Assert.Equal(PipelineStatus.Finished, run.Status);
            Assert.Contains("rejected twice", _console.ToString());
            var steps = await StepsOf(services, run.Id);
            var secondResult = Assert.Single(steps, step => step.StepId == "second").OutputsJson;
            Assert.Contains("\"by\": \"bob\"", secondResult);
            Assert.Contains("\"comment\": null", secondResult);
            Assert.DoesNotContain("go ahead", secondResult);
        }

        [Fact]
        public async Task An_approval_nobody_decided_in_time_goes_down_Expired_and_a_late_decision_is_refused()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "late", new AskForApproval { Question = new("Ship it?"), DecideWithin = new("2 hours") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("late"));

            var approval = Assert.Single(await ListAsync(services, null, PipelinesPermissions.View));
            Assert.Equal(_clock.UtcNow.AddHours(2).UtcDateTime, approval.ExpiresOnUtc);

            // Not yet: the task leaves it.
            var task = services.GetRequiredService<DueWaitsTask>();
            _clock.UtcNow = _clock.UtcNow.AddMinutes(119);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Running, (await RunOf(services, definitionId)).Status);

            // Past its time: a person's decision is refused, saying why; the task sends the run down Expired.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DecideAsync(services, approval.Id, approve: true, comment: null));
            Assert.Contains("goes on down Expired", refused.Message);
            await task.ExecuteAsync(CancellationToken.None);
            var run = await RunOf(services, definitionId);
            Assert.Equal(PipelineStatus.Finished, run.Status);
            Assert.Contains("expired late", _console.ToString());
            Assert.DoesNotContain("approved late", _console.ToString());
            Assert.Contains("\"decision\": \"Expired\"", Assert.Single(await StepsOf(services, run.Id), step => step.StepId == "ask").OutputsJson);
            Assert.Empty(await ListAsync(services, null, PipelinesPermissions.View));

            // An approval with no time to decide is never expired by the task.
            var open = await PublishAsync(services, "open", new AskForApproval { Question = new("Whenever?") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("open"));
            _clock.UtcNow = _clock.UtcNow.AddDays(400);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Running, (await RunOf(services, open)).Status);
        }

        // Found driving the editor's Stop run: a run cancelled while it waited left its approval stored - listed as waiting, and a
        // decision on it answered "Approved" though the engine would not go on with a finished run. A cancel clears what the run
        // waited on now; what an older cancel left is neither listed nor decided, and the task clears it at its next tick.
        [Fact]
        public async Task A_cancelled_run_leaves_no_approval_to_list_or_decide()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<IBookmarkStore>();
            var definitionId = await PublishAsync(services, "stop", new AskForApproval { Question = new("Go on?"), DecideWithin = new("1 hour") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("stop"));
            var run = await RunOf(services, definitionId);
            var approval = Assert.Single(await ListAsync(services, null, PipelinesPermissions.View));
            var left = Assert.Single(await store.FindManyAsync(new BookmarkFilter { PipelineInstanceId = run.Id }));

            Assert.True(await new CancelRunCommandHandler(services.GetRequiredService<IPipelineRuntime>(), services.GetRequiredService<IPipelineInstanceStore>(),
                new Stomme.Modules.Pipelines.Reliability.PipelineRunStops(), _clock, services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>()).Handle(new CancelRunCommand(run.Id), CancellationToken.None));
            Assert.Equal(PipelineSubStatus.Cancelled, (await RunOf(services, definitionId)).SubStatus);
            Assert.Empty(await store.FindManyAsync(new BookmarkFilter { PipelineInstanceId = run.Id })); // cleared with the run
            Assert.Empty(await ListAsync(services, null, PipelinesPermissions.View));
            Assert.Null(await DecideAsync(services, approval.Id, approve: true, comment: null));

            // What an older cancel left behind: not listed, a decision refused saying why - and cleared at the task's next tick,
            // past its time to decide as well, the finished run never resumed down Expired.
            await store.SaveAsync(left);
            Assert.Empty(await ListAsync(services, null, PipelinesPermissions.View));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DecideAsync(services, approval.Id, approve: true, comment: null));
            Assert.Equal("The run has ended (cancelled); there is nothing left to decide.", refused.Message);
            _clock.UtcNow = _clock.UtcNow.AddHours(2);
            await services.GetRequiredService<DueWaitsTask>().ExecuteAsync(CancellationToken.None);
            Assert.Empty(await store.FindManyAsync(new BookmarkFilter { PipelineInstanceId = run.Id }));
            Assert.DoesNotContain("expired stop", _console.ToString());
            Assert.Equal(PipelineSubStatus.Cancelled, (await RunOf(services, definitionId)).SubStatus);
        }

        // BL-229: Stop ends the whole run, as n8n's does - a sub-pipeline it started, waiting for an approval, is cancelled with it;
        // stopped alone, the caller left the child waiting, and a decision on it would have run the child's work.
        [Fact]
        public async Task A_run_stopped_stops_the_runs_it_started()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            async Task<string> PublishFlowAsync(string name, Flowchart flowchart)
            {
                var definition = await publisher.NewAsync(flowchart);
                definition.Name = name;
                var published = await publisher.PublishAsync(definition);
                Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
                return definition.DefinitionId;
            }

            var ask = new AskForApproval { Id = "ask", Question = new("Send it?") };
            var send = new WriteLine("the child sent it") { Id = "send" };
            var child = await PublishFlowAsync("Child", new Flowchart { Id = "child-flow", Steps = { ask, send }, Connections = { new Connection(new Endpoint(ask, PipelineApprovals.Approved), new Endpoint(send)) } });

            var trigger = new Webhook { WebhookName = new("parent"), Id = "trigger" };
            trigger.SetCanStartPipeline(true);
            var call = new ExecutePipeline { Id = "call", PipelineDefinitionId = new(child), WaitForCompletion = new(true) };
            var after = new WriteLine("the parent went on") { Id = "after" };
            var parent = await PublishFlowAsync("Parent", new Flowchart { Id = "parent-flow", Steps = { trigger, call, after }, Connections = { new Connection(new Endpoint(trigger), new Endpoint(call)), new Connection(new Endpoint(call), new Endpoint(after)) } });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("parent"));

            // Both waiting: the parent on its child, the child on its approval.
            var parentRun = await RunOf(services, parent);
            var childRun = await RunOf(services, child);
            Assert.Equal((PipelineStatus.Running, PipelineStatus.Running), (parentRun.Status, childRun.Status));
            Assert.Equal(parentRun.Id, childRun.ParentPipelineInstanceId);
            var approval = Assert.Single(await ListAsync(services, null, PipelinesPermissions.View));

            Assert.True(await new CancelRunCommandHandler(services.GetRequiredService<IPipelineRuntime>(), services.GetRequiredService<IPipelineInstanceStore>(),
                new Stomme.Modules.Pipelines.Reliability.PipelineRunStops(), _clock, services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>()).Handle(new CancelRunCommand(parentRun.Id), CancellationToken.None));
            Assert.Equal(PipelineSubStatus.Cancelled, (await RunOf(services, parent)).SubStatus);
            Assert.Equal(PipelineSubStatus.Cancelled, (await RunOf(services, child)).SubStatus);
            Assert.Empty(await ListAsync(services, null, PipelinesPermissions.View));
            Assert.Null(await DecideAsync(services, approval.Id, approve: true, comment: null));
            Assert.DoesNotContain("the child sent it", _console.ToString());
            Assert.DoesNotContain("the parent went on", _console.ToString());
        }

        [Fact]
        public async Task A_question_left_empty_or_a_time_to_decide_unreadable_or_too_long_fails_the_step()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var sender = services.GetRequiredService<IStimulusSender>();

            var unreadable = await PublishAsync(services, "soon", new AskForApproval { Question = new("Now?"), DecideWithin = new("soon") });
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("soon"));
            Assert.Equal(PipelineSubStatus.Faulted, (await RunOf(services, unreadable)).SubStatus);

            _options.Waits.MaxDays = 7;
            var tooLong = await PublishAsync(services, "toolong", new AskForApproval { Question = new("Next month?"), DecideWithin = new("2 weeks") });
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("toolong"));
            Assert.Equal(PipelineSubStatus.Faulted, (await RunOf(services, tooLong)).SubStatus);
            _options.Waits.MaxDays = new WaitOptions().MaxDays;

            var empty = await PublishAsync(services, "empty", new AskForApproval { Question = new("  ") });
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("empty"));
            Assert.Equal(PipelineSubStatus.Faulted, (await RunOf(services, empty)).SubStatus);
            Assert.Empty(await ListAsync(services, null, PipelinesPermissions.View));
        }

        private static async Task<IReadOnlyList<PipelineRunLogLine>> LogOf(IServiceProvider services, string runId) =>
            await new GetRunLogQueryHandler(services.GetRequiredService<IPipelineExecutionLogStore>()).Handle(new GetRunLogQuery(runId), CancellationToken.None);

        // n8n's approval node SENDS the request: the people "Notify" names are told what is asked, by when, and where to decide.
        [Fact]
        public async Task The_people_it_names_are_told_what_is_asked_by_when_and_where_to_decide()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "told", new AskForApproval
            {
                Question = new("Pay invoice 42?"),
                DecideWithin = new("2 days"),
                Notify = new("role:Approvers\nuser:7\n  user:11  \nemail:Boss@example.test\nemail:boss@example.test"),
                NotifyBy = new("InApp, email"),
            });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("told"));
            var run = await RunOf(services, definitionId);

            var sent = Assert.Single(_dispatcher.Sent);
            Assert.Equal("generic.message", sent.TemplateKey);
            // The role is the members who hold it now (an ended, a deleted and a future membership left out); each user and address once.
            Assert.Equal(new[] { "User:11", "User:7", "Email:Boss@example.test" }, sent.Recipients.Select(recipient => recipient.Kind + ":" + recipient.Value));
            Assert.Equal(new[] { Stomme.Models.Notifications.NotificationChannelKind.InApp, Stomme.Models.Notifications.NotificationChannelKind.Email }, sent.Channels);
            Assert.Equal(PipelineApprovalNotices.Topic, sent.Topic);
            var subject = Assert.Single(sent.Parameters!, parameter => parameter.Name == "subject").Value;
            var body = Assert.Single(sent.Parameters!, parameter => parameter.Name == "body").Value;
            Assert.Equal("Approval needed: Approves told", subject);
            Assert.StartsWith("Pay invoice 42?", body);
            Assert.Contains("To be decided by 2026-10-08 09:00 UTC.", body);
            Assert.EndsWith("Decide here: https://example.test/admin/pipelines/*/59/Run?id=" + run.Id, body);
            Assert.Contains(await LogOf(services, run.Id), line => line.EventName == "Notified" && line.Message == "3 recipients told by InApp and Email.");

            // It still waits for the decision.
            Assert.Equal(PipelineStatus.Running, run.Status);
            Assert.Single(await ListAsync(services, run.Id, PipelinesPermissions.View));
        }

        // Found driving the starter: a fresh site has no Administrators member - its administrator is the host, holding Host
        // Users alone - so "role:Administrators" told no one. Administrators are the site's operators, hosts included.
        [Fact]
        public async Task The_Administrators_are_the_sites_operators_the_host_included()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, "operators", new AskForApproval { Question = new("Pay?"), Notify = new("role:administrators") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("operators"));

            Assert.Equal(new[] { "User:1" }, Assert.Single(_dispatcher.Sent).Recipients.Select(recipient => recipient.Kind + ":" + recipient.Value));
        }

        // A notice that cannot go is a line of the run's log, never a failure: the run still waits for its decision.
        [Theory]
        [InlineData("role:Nobody", null, null, "No one holds the roles it names on this site.")]
        [InlineData("pager:555", null, null, "'pager:555' is not role:<name>, user:<id> or email:<address>.")]
        [InlineData("user:7", "Sms", null, "'Sms' is not a way to notify - write InApp, Email, or both.")]
        [InlineData("email:someone@elsewhere.test", null, "External recipients are not allowed on this site.", "External recipients are not allowed on this site.")]
        public async Task A_notice_that_cannot_go_is_logged_and_the_run_still_waits(string notify, string? notifyBy, string? refusal, string logged)
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            _dispatcher.Refusal = refusal;
            var definitionId = await PublishAsync(services, "untold", new AskForApproval { Question = new("Ship it?"), Notify = new(notify), NotifyBy = notifyBy is null ? null : new(notifyBy) });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("untold"));

            var run = await RunOf(services, definitionId);
            Assert.Equal(PipelineStatus.Running, run.Status);
            Assert.Empty(_dispatcher.Sent);
            Assert.Contains(await LogOf(services, run.Id), line => line.EventName == "Not notified" && line.Message == logged);
            Assert.Single(await ListAsync(services, run.Id, PipelinesPermissions.View));
        }

        [Fact]
        public async Task A_comment_too_long_is_refused_and_an_unknown_approval_is_none()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await Assert.ThrowsAsync<ArgumentException>(() => DecideAsync(services, "any", approve: true, comment: new string('x', PipelineApprovals.MaxCommentLength + 1)));
            Assert.Null(await DecideAsync(services, "no-such-approval", approve: true, comment: null));
            Assert.Null(await DecideAsync(services, "", approve: true, comment: null));
        }

        [Fact]
        public void Deciding_takes_its_own_key_and_listing_the_view_key()
        {
            string Key(Type message) => Assert.Single(message.GetCustomAttributes<Stomme.Security.Abstractions.RequiresAttribute>()).Permission!;
            Assert.Equal(PipelinesPermissions.Approve, Key(typeof(DecideApprovalCommand)));
            Assert.Equal(PipelinesPermissions.View, Key(typeof(ListApprovalsQuery)));
            Assert.Contains(PipelinesPermissions.Approve, PipelinesPermissions.All);
            Assert.Contains(PipelinesPermissions.Declarations, declaration => declaration.Key == PipelinesPermissions.Approve);
        }

        [Fact]
        public void An_approval_is_read_back_whatever_form_the_store_kept_it_in()
        {
            var kept = new ApprovalStimulus("ask", "Manager OK", "Pay?", "1200 EUR", "2026-10-06T09:00:00.0000000Z", "2026-10-08T09:00:00.0000000Z");
            Assert.Same(kept, AskForApproval.Of(kept));
            Assert.Equal(kept, AskForApproval.Of(JsonSerializer.SerializeToElement(kept)));
            Assert.Equal(kept, AskForApproval.Of(JsonSerializer.SerializeToElement(kept, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            Assert.Equal(kept, AskForApproval.Of(new Dictionary<string, object?>
            {
                ["stepId"] = "ask", ["stepName"] = "Manager OK", ["question"] = "Pay?", ["details"] = "1200 EUR",
                ["requestedAt"] = "2026-10-06T09:00:00.0000000Z", ["expiresAt"] = "2026-10-08T09:00:00.0000000Z",
            }));
            Assert.Null(AskForApproval.Of(null));
            Assert.Null(AskForApproval.Of("a wait's moment"));
            Assert.Null(AskForApproval.Of(JsonSerializer.SerializeToElement(new WaitStimulus("2026-10-06T11:00:00Z"))));
            Assert.Equal(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero), AskForApproval.MomentOf(kept.ExpiresAt));
            Assert.Null(AskForApproval.MomentOf(null));

            // What is asked is kept to a bound, saying where it was cut.
            var cut = PipelineApprovals.Cut(new string('q', PipelineApprovals.MaxTextLength + 10));
            Assert.EndsWith("… (cut)", cut);
            Assert.Equal(PipelineApprovals.MaxTextLength + "… (cut)".Length, cut.Length);
        }
    }
}
