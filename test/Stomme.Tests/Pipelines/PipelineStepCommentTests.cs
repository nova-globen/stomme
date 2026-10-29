#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Erasure;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Audit;
using Stomme.Modules.Pipelines.Comments;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Comments on a pipeline's steps: kept per site on the storage-blind module seam (here its FILE store - a real
    /// write and read per scope), written by anyone who can see the pipeline, deleted by their author or a manager, recorded in
    /// the activity ledger by step and never by text, gone with their pipeline, and erased with their author.
    /// </summary>
    public sealed class PipelineStepCommentTests : IAsyncLifetime
    {
        private static readonly DateTime Start = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-step-comments-" + Guid.NewGuid().ToString("N"));
        private readonly SiteState _site = new() { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } };
        private readonly Reader _user = new() { UserId = 7, UserName = "ada" };
        private readonly Guard _guard = new();
        private readonly Clock _clock = new();
        private readonly SitePeople _people = new();
        private readonly RecordingDispatcher _dispatcher = new();
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = _root,
                })
                .Build());
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
            services.AddScoped<IRequestHandler<DeletePipelineCommand, bool>, DeletePipelineCommandHandler>();
            services.AddSingleton<ICurrentUser>(_user);
            services.AddSingleton<IAccessGuard>(_guard);
            services.AddSingleton<ITenantContext>(new OneTenant());
            services.AddSingleton(_site);
            services.AddSingleton<TimeProvider>(_clock);
            // Who is on the site and who may see pipelines, and the Notifications module's port - recorded, never delivered.
            services.AddSingleton<IUserRoleRepository>(_people);
            services.AddSingleton<Stomme.Infrastructure.Security.IStoredRowPermissionEvaluator>(_people);
            services.AddSingleton<IPageModuleRepository>(new NoPages());
            services.AddSingleton<Stomme.Notifications.INotificationDispatcher>(_dispatcher);
            // The module's own composition, as its startup runs it: the ledger, then the comments.
            PipelineAuditRegistration.Register(services);
            PipelineStepCommentRegistration.Register(services);
            _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // a leftover temp folder is not worth failing a green run over
            }
        }

        [Fact]
        public async Task A_comment_is_kept_on_its_step_listed_oldest_first_and_shown_as_its_reader_may_act_on_it()
        {
            var orders = await MakeAsync("Orders");
            var first = (await SendAsync(new AddStepCommentCommand(orders, " say ", "  Why does this say hello?  ")))!;
            _clock.Advance(TimeSpan.FromMinutes(1));
            As(8, "bob");
            await SendAsync(new AddStepCommentCommand(orders, "say", "Because the customer asked."));
            _clock.Advance(TimeSpan.FromMinutes(1));
            As(7, "ada");
            await SendAsync(new AddStepCommentCommand(orders, "other", "And this one?"));

            Assert.Equal(("say", "ada", "Why does this say hello?", Start, false), (first.StepId, first.UserName, first.Text, first.CreatedOnUtc, first.Resolved));
            var listed = await SendAsync(new ListStepCommentsQuery(orders));
            Assert.Equal(new[] { ("say", "ada", true, true), ("say", "bob", false, false), ("other", "ada", true, true) },
                listed.Select(comment => (comment.StepId, comment.UserName, comment.Mine, comment.CanDelete)).ToArray());
            Assert.All(listed, comment => Assert.Equal(DateTimeKind.Utc, comment.CreatedOnUtc.Kind));

            // A manager may delete anyone's; the store keeps the site, the tenant and the pipeline with each.
            _guard.Manager = true;
            Assert.All(await SendAsync(new ListStepCommentsQuery(orders)), comment => Assert.True(comment.CanDelete));
            var stored = await InScopeAsync(services => services.GetRequiredService<IRepository<PipelineStepCommentAggregate, Guid>>().GetByIdAsync(first.Id));
            Assert.Equal((1, orders, 7, TenantId.Default), (stored!.SiteId, stored.DefinitionId, stored.UserId, stored.TenantId));

            // Another pipeline's discussion, and another site's, are their own.
            var invoices = await MakeAsync("Invoices");
            Assert.Empty(await SendAsync(new ListStepCommentsQuery(invoices)));

            // The pipelines list counts what is open, by pipeline - a resolved comment is not; a pipeline with none is not listed.
            await SendAsync(new SetStepCommentResolvedCommand(orders, first.Id, true));
            Assert.Equal(new Dictionary<string, int> { [orders] = 2 }, await SendAsync(new ListOpenCommentCountsQuery()));
            await SendAsync(new SetStepCommentResolvedCommand(orders, first.Id, false));
            _site.Alias!.SiteId = 2;
            Assert.Empty(await SendAsync(new ListStepCommentsQuery(orders)));
            Assert.Null(await SendAsync(new SetStepCommentResolvedCommand(orders, first.Id, true))); // not this site's comment
            Assert.Null(await SendAsync(new DeleteStepCommentCommand(orders, first.Id)));
            _site.Alias.SiteId = 1;
            Assert.Null(await SendAsync(new SetStepCommentResolvedCommand(invoices, first.Id, true))); // not that pipeline's
            Assert.Null(await SendAsync(new DeleteStepCommentCommand(invoices, first.Id)));
            Assert.Equal(3, (await SendAsync(new ListStepCommentsQuery(orders))).Count);
        }

        [Fact]
        public async Task A_comment_is_refused_when_empty_too_long_unsigned_or_on_a_pipeline_the_site_does_not_have()
        {
            var orders = await MakeAsync("Orders");
            await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(new AddStepCommentCommand(orders, "say", "   ")));
            await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(new AddStepCommentCommand(orders, "say", new string('x', PipelineStepCommentLimits.MaxText + 1))));
            await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(new AddStepCommentCommand(orders, " ", "On no step")));
            Assert.Null(await SendAsync(new AddStepCommentCommand("no-such-pipeline", "say", "Hello?")));
            Assert.Null(await SendAsync(new AddStepCommentCommand("", "say", "Hello?")));
            As(null, null);
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new AddStepCommentCommand(orders, "say", "Anonymous")));
            As(7, "ada");
            Assert.Empty(await SendAsync(new ListStepCommentsQuery(orders)));

            // The longest allowed is kept whole.
            var longest = new string('y', PipelineStepCommentLimits.MaxText);
            Assert.Equal(longest, (await SendAsync(new AddStepCommentCommand(orders, "say", longest)))!.Text);
        }

        [Fact]
        public async Task Anyone_who_sees_the_pipeline_resolves_and_reopens_a_comment_and_only_its_author_or_a_manager_deletes_it()
        {
            var orders = await MakeAsync("Orders");
            var adas = (await SendAsync(new AddStepCommentCommand(orders, "say", "Should this be a template?")))!;

            As(8, "bob");
            _clock.Advance(TimeSpan.FromMinutes(5));
            var resolved = (await SendAsync(new SetStepCommentResolvedCommand(orders, adas.Id, true)))!;
            Assert.Equal((true, "bob", Start.AddMinutes(5)), (resolved.Resolved, resolved.ResolvedBy, resolved.ResolvedOnUtc));
            _clock.Advance(TimeSpan.FromMinutes(5));
            var again = (await SendAsync(new SetStepCommentResolvedCommand(orders, adas.Id, true)))!; // resolved already: unchanged
            Assert.Equal((true, "bob", Start.AddMinutes(5)), (again.Resolved, again.ResolvedBy, again.ResolvedOnUtc));
            var reopened = (await SendAsync(new SetStepCommentResolvedCommand(orders, adas.Id, false)))!;
            Assert.Equal((false, (string?)null, (DateTime?)null), (reopened.Resolved, reopened.ResolvedBy, reopened.ResolvedOnUtc));

            // Bob may not delete Ada's comment - until he may change pipelines.
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new DeleteStepCommentCommand(orders, adas.Id)));
            Assert.Single(await SendAsync(new ListStepCommentsQuery(orders)));
            var bobs = (await SendAsync(new AddStepCommentCommand(orders, "say", "Mine to take back")))!;
            Assert.Equal("Mine to take back", (await SendAsync(new DeleteStepCommentCommand(orders, bobs.Id)))!.Text); // his own, answered as it was
            _guard.Manager = true;
            Assert.NotNull(await SendAsync(new DeleteStepCommentCommand(orders, adas.Id)));
            Assert.Null(await SendAsync(new DeleteStepCommentCommand(orders, adas.Id))); // gone already
            Assert.Null(await SendAsync(new SetStepCommentResolvedCommand(orders, adas.Id, true)));
            Assert.Empty(await SendAsync(new ListStepCommentsQuery(orders)));
        }

        [Fact]
        public async Task A_pipeline_deleted_for_good_takes_its_discussion_with_it_and_no_other()
        {
            var gone = await MakeAsync("Gone");
            var kept = await MakeAsync("Kept");
            await SendAsync(new AddStepCommentCommand(gone, "say", "One"));
            await SendAsync(new AddStepCommentCommand(gone, "say", "Two"));
            await SendAsync(new AddStepCommentCommand(kept, "say", "Stays"));

            _guard.Manager = true;
            Assert.True(await SendAsync(new DeletePipelineCommand(gone)));

            Assert.Empty(await SendAsync(new ListStepCommentsQuery(gone)));
            Assert.Equal("Stays", Assert.Single(await SendAsync(new ListStepCommentsQuery(kept))).Text);
        }

        [Fact]
        public async Task The_ledger_names_the_step_of_each_change_and_never_its_text()
        {
            var orders = await MakeAsync("Orders");
            var comment = (await SendAsync(new AddStepCommentCommand(orders, "say", "The secret plan")))!;
            await SendAsync(new SetStepCommentResolvedCommand(orders, comment.Id, true));
            await SendAsync(new SetStepCommentResolvedCommand(orders, comment.Id, false));
            await SendAsync(new DeleteStepCommentCommand(orders, comment.Id));
            Assert.Null((await SendAsync(new AddStepCommentCommand(orders, "not-saved-yet", "On a step the pipeline does not have yet")))!.StepName); // its id says it
            Assert.Null(await SendAsync(new AddStepCommentCommand("no-such-pipeline", "say", "Nowhere"))); // nothing done, nothing recorded

            var ledger = (await InScopeAsync(services => services.GetRequiredService<IPipelineAuditRepository>().ListBySiteAsync(1)))
                .OrderBy(entry => entry.OccurredOnUtc).ThenBy(entry => entry.Action, StringComparer.Ordinal).ToList();
            Assert.Equal(
                new[] { ("commented on a step", "on step 'Say'"), ("commented on a step", "on step 'not-saved-yet'"), ("deleted a comment", "on step 'Say'"), ("reopened a comment", "on step 'Say'"), ("resolved a comment", "on step 'Say'") },
                ledger.Select(entry => (entry.Action, Detail: entry.Detail ?? "")).OrderBy(line => line.Action, StringComparer.Ordinal).ThenBy(line => line.Detail, StringComparer.Ordinal).ToArray());
            Assert.All(ledger, entry => Assert.Equal((orders, "Orders", 7), (entry.DefinitionId, entry.PipelineName!, entry.UserId)));
            Assert.DoesNotContain(ledger, entry => (entry.Detail ?? "").Contains("secret", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Erasure_deletes_the_subjects_comments_and_forgets_them_as_the_one_who_resolved_another()
        {
            var orders = await MakeAsync("Orders");
            await SendAsync(new AddStepCommentCommand(orders, "say", "Ada's first"));
            await SendAsync(new AddStepCommentCommand(orders, "other", "Ada's second"));
            As(8, "bob");
            var bobs = (await SendAsync(new AddStepCommentCommand(orders, "say", "Bob's")))!;
            As(7, "ada");
            _clock.Advance(TimeSpan.FromMinutes(3));
            await SendAsync(new SetStepCommentResolvedCommand(orders, bobs.Id, true));

            // Her data package carries her own comments - the step and the text, never who resolved one (someone else's name).
            var exported = await InScopeAsync(services => services.GetServices<Stomme.Portability.ISubjectDataContributor>()
                .Single(contributor => contributor.SubjectDataType == PipelineStepCommentSubjectContributor.Type)
                .ExportForSubjectAsync(new Stomme.Portability.SubjectScope(1, 7)));
            var carried = exported.Select(entity => System.Text.Json.JsonDocument.Parse(entity.PayloadJson).RootElement)
                .Select(payload => (payload.GetProperty("pipeline").GetString(), payload.GetProperty("step").GetString(), payload.GetProperty("text").GetString()))
                .OrderBy(item => item.Item3, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { (orders, "say", "Ada's first"), (orders, "other", "Ada's second") }, carried);
            Assert.DoesNotContain(exported, entity => entity.PayloadJson.Contains("bob", StringComparison.OrdinalIgnoreCase));

            var participant = await InScopeAsync(services => Task.FromResult(services.GetServices<ISubjectErasureParticipant>().OfType<PipelineStepCommentErasureParticipant>().Single()));
            var outcome = Assert.Single(await InScopeAsync(services => services.GetServices<ISubjectErasureParticipant>()
                .OfType<PipelineStepCommentErasureParticipant>().Single().EraseAsync(new ErasureSubject(1, 7, null))));
            Assert.Equal(("PipelineStepCommentAggregate", ErasureHandling.Delete, 2), (outcome.Store, outcome.Handling, outcome.Affected));
            Assert.Equal(participant.Declarations.Single().Store, outcome.Store);

            var left = Assert.Single(await SendAsync(new ListStepCommentsQuery(orders)));
            Assert.Equal(("Bob's", true, (string?)null, (DateTime?)Start.AddMinutes(3)), (left.Text, left.Resolved, left.ResolvedBy, left.ResolvedOnUtc));
            var stored = await InScopeAsync(services => services.GetRequiredService<IRepository<PipelineStepCommentAggregate, Guid>>().GetByIdAsync(bobs.Id));
            Assert.Null(stored!.ResolvedByUserId);

            // Idempotent: a second run finds nothing of hers.
            Assert.Equal(0, Assert.Single(await InScopeAsync(services => services.GetServices<ISubjectErasureParticipant>()
                .OfType<PipelineStepCommentErasureParticipant>().Single().EraseAsync(new ErasureSubject(1, 7, null)))).Affected);
        }

        // @mentions: a comment tells the people it names who can see pipelines, in the app and by e-mail, with the
        // comment and its step; anyone else named is said back as told nobody, and the author is never told.
        [Fact]
        public async Task A_comment_tells_the_people_it_mentions_who_can_see_pipelines_and_no_one_else()
        {
            var orders = await MakeAsync("Orders");
            var added = (await SendAsync(new AddStepCommentCommand(orders, "say", "@Bob, can you check this? @cy @nobody - and @ada, @BOB.")))!;

            Assert.Equal(new[] { "Bob Builder" }, added.Told);
            Assert.Equal(new[] { "cy", "nobody" }, added.NotTold); // cy is on the site but cannot see pipelines: said as nobody
            var request = Assert.Single(_dispatcher.Requests);
            Assert.Equal(new[] { "8" }, request.Recipients.Select(recipient => recipient.Value));
            Assert.All(request.Recipients, recipient => Assert.Equal(NotificationRecipientKind.User, recipient.Kind));
            Assert.Equal(new[] { NotificationChannelKind.InApp, NotificationChannelKind.Email }, request.Channels);
            Assert.Equal(PipelineMentionNotifier.Topic, request.Topic);
            var subject = request.Parameters!.Single(parameter => parameter.Name == "subject").Value;
            var body = request.Parameters!.Single(parameter => parameter.Name == "body").Value;
            Assert.Equal("ada mentioned you on 'Orders'", subject);
            Assert.Contains("step 'Say' of 'Orders'", body, StringComparison.Ordinal);
            Assert.Contains("@Bob, can you check this?", body, StringComparison.Ordinal);

            // Only those who can see pipelines are offered to mention, by display name.
            Assert.Equal(new[] { "ada", "Bob Builder" }, (await SendAsync(new ListMentionablePeopleQuery())).Select(person => person.DisplayName));

            // A comment naming nobody tells nobody; one whose telling fails is kept, its names said back as told nobody.
            Assert.Empty((await SendAsync(new AddStepCommentCommand(orders, "say", "No one named here - mail@example.com is no mention.")))!.Told);
            Assert.Single(_dispatcher.Requests);
            _dispatcher.Failing = true;
            var failed = (await SendAsync(new AddStepCommentCommand(orders, "say", "@bob again")))!;
            Assert.Equal((0, "Bob Builder"), (failed.Told.Count, Assert.Single(failed.NotTold)));
            Assert.Equal(3, (await SendAsync(new ListStepCommentsQuery(orders))).Count);
        }

        [Theory]
        [InlineData("@ada, @bob.", "ada|bob")]
        [InlineData("mail@example.com and @@x are no mention", "")]
        [InlineData("@Ada @ada @ADA", "Ada")]
        [InlineData("cc @first.last-name and @ada@kafe.example", "first.last-name|ada@kafe.example")]
        [InlineData("(@ada)", "ada")]
        public void A_mention_is_an_at_starting_a_word_then_a_user_name(string text, string names) =>
            Assert.Equal(names, string.Join("|", PipelineStepMentionNames.Of(text)));

        [Fact]
        public void A_comment_is_written_whole_or_not_at_all()
        {
            var now = Start;
            Assert.Throws<ArgumentException>(() => PipelineStepCommentAggregate.Write(Guid.Empty, TenantId.Default, 1, "def", "step", 7, "ada", "x", now));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipelineStepCommentAggregate.Write(Guid.NewGuid(), TenantId.Default, 0, "def", "step", 7, "ada", "x", now));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipelineStepCommentAggregate.Write(Guid.NewGuid(), TenantId.Default, 1, "def", "step", 0, "ada", "x", now));
            Assert.Throws<ArgumentException>(() => PipelineStepCommentAggregate.Write(Guid.NewGuid(), TenantId.Default, 1, " ", "step", 7, "ada", "x", now));
            Assert.Throws<ArgumentException>(() => PipelineStepCommentAggregate.Write(Guid.NewGuid(), TenantId.Default, 1, "def", new string('s', PipelineStepCommentLimits.MaxStepId + 1), 7, "ada", "x", now));
            Assert.Throws<ArgumentException>(() => PipelineStepCommentAggregate.Write(Guid.NewGuid(), TenantId.Default, 1, "def", "step", 7, "ada", "x", DateTime.Now));
            var comment = PipelineStepCommentAggregate.Write(Guid.NewGuid(), TenantId.Default, 1, " def ", " step ", 7, new string('n', 300), " x ", now);
            Assert.Equal(("def", "step", "x", PipelineStepCommentAggregate.MaximumUserNameLength), (comment.DefinitionId, comment.StepId, comment.Text, comment.UserName!.Length));
            Assert.False(comment.SetResolved(false, 7, "ada", now)); // open already
            Assert.Throws<ArgumentException>(() => comment.SetResolved(true, 7, "ada", DateTime.Now));
            Assert.True(comment.SetResolved(true, 7, "ada", now));
        }

        private void As(int? userId, string? userName)
        {
            _user.UserId = userId;
            _user.UserName = userName;
        }

        private async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
        }

        private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
        {
            using var scope = _services.CreateScope();
            return await work(scope.ServiceProvider);
        }

        private async Task<string> MakeAsync(string name)
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Inputs = { ["Text"] = "hello" } } } };
            var definition = await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(
                PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>())));
            definition.Name = name;
            await publisher.SaveDraftAsync(definition);
            return definition.DefinitionId;
        }

        private sealed class Reader : ICurrentUser
        {
            public int? UserId { get; set; }

            public string? UserName { get; set; }

            public bool IsAuthenticated => UserId is not null;

            public IReadOnlySet<string> Roles => new HashSet<string>();

            public int SiteId => 1;
        }

        // Every key held but the manage key, which is held when Manager says so.
        private sealed class Guard : IAccessGuard
        {
            public bool Manager { get; set; }

            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) =>
                Task.FromResult(permission != global::Stomme.Pipelines.PipelinesPermissions.Manage || Manager);

            public Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        private sealed class OneTenant : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;

            public bool IsSystemScope => false;
        }

        private sealed class Clock : TimeProvider
        {
            private DateTimeOffset _now = new(Start);

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by) => _now = _now.Add(by);
        }
    }

    /// <summary>The site's people for the mention tests: ada (7) and Bob (8) may see pipelines, cy (9) may not, gone (10) is deleted.</summary>
    internal sealed class SitePeople : IUserRoleRepository, Stomme.Infrastructure.Security.IStoredRowPermissionEvaluator
    {
        private static readonly User[] Users =
        {
            new() { UserId = 7, Username = "ada", DisplayName = "ada" },
            new() { UserId = 8, Username = "bob", DisplayName = "Bob Builder" },
            new() { UserId = 9, Username = "cy", DisplayName = "Cy" },
            new() { UserId = 10, Username = "gone", DisplayName = "Gone", IsDeleted = true },
        };

        public Task<bool> IsAuthorizedAsync(int siteId, int userId, string permissionKey, CancellationToken ct = default) =>
            Task.FromResult(siteId == 1 && permissionKey == global::Stomme.Pipelines.PipelinesPermissions.View && userId is 7 or 8 or 10);

        public Task<IEnumerable<UserRole>> GetUserRolesAsync(int siteId) =>
            Task.FromResult<IEnumerable<UserRole>>(Users.SelectMany(user => new[] { new UserRole { UserId = user.UserId, User = user }, new UserRole { UserId = user.UserId, User = user } }).ToList());

        public Task<IEnumerable<UserRole>> GetUserRolesAsync(int userId, int siteId) => throw new NotSupportedException();

        public Task<IEnumerable<UserRole>> GetUserRolesAsync(string roleName, int siteId) => throw new NotSupportedException();

        public Task<UserRole> AddUserRoleAsync(UserRole userRole) => throw new NotSupportedException();

        public Task<UserRole> UpdateUserRoleAsync(UserRole userRole) => throw new NotSupportedException();

        public Task<UserRole> GetUserRoleAsync(int userRoleId) => throw new NotSupportedException();

        public Task<UserRole> GetUserRoleAsync(int userRoleId, bool tracking) => throw new NotSupportedException();

        public Task<UserRole> GetUserRoleAsync(int userId, int roleId) => throw new NotSupportedException();

        public Task<UserRole> GetUserRoleAsync(int userId, int roleId, bool tracking) => throw new NotSupportedException();

        public Task DeleteUserRoleAsync(int userRoleId) => throw new NotSupportedException();

        public Task DeleteUserRolesAsync(int userId) => throw new NotSupportedException();
    }

    /// <summary>A site whose Pipelines module is placed on no page: a notice carries no link.</summary>
    internal sealed class NoPages : IPageModuleRepository
    {
        public Task<IEnumerable<PageModule>> GetPageModulesAsync(int siteId) => Task.FromResult(Enumerable.Empty<PageModule>());

        public Task<PageModule> AddPageModuleAsync(PageModule pageModule) => throw new NotSupportedException();

        public Task<PageModule> UpdatePageModuleAsync(PageModule pageModule) => throw new NotSupportedException();

        public Task<PageModule> GetPageModuleAsync(int pageModuleId) => throw new NotSupportedException();

        public Task<PageModule> GetPageModuleAsync(int pageModuleId, bool tracking) => throw new NotSupportedException();

        public Task<PageModule> GetPageModuleAsync(int pageId, int moduleId) => throw new NotSupportedException();

        public Task DeletePageModuleAsync(int pageModuleId) => throw new NotSupportedException();
    }

    /// <summary>The Notifications module's port, recorded; it throws when told to fail.</summary>
    internal sealed class RecordingDispatcher : Stomme.Notifications.INotificationDispatcher
    {
        public List<NotificationDispatchRequest> Requests { get; } = new();

        public bool Failing { get; set; }

        public Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default)
        {
            if (Failing)
            {
                throw new InvalidOperationException("The notification store is down.");
            }

            Requests.Add(request);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    /// <summary>A site with no discussion - for the suites that delete a pipeline and are about something else.</summary>
    internal sealed class NoStepComments : IPipelineStepCommentRepository, IUnitOfWork
    {
        public Task AddAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpdateAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PipelineStepCommentAggregate?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<PipelineStepCommentAggregate?>(null);

        public Task<IReadOnlyList<PipelineStepCommentAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PipelineStepCommentAggregate>>(Array.Empty<PipelineStepCommentAggregate>());

        public Task<IReadOnlyList<PipelineStepCommentAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PipelineStepCommentAggregate>>(Array.Empty<PipelineStepCommentAggregate>());

        public Task<int> DeleteByPipelineAsync(int siteId, string definitionId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);

        public void Dispose()
        {
        }
    }
}
