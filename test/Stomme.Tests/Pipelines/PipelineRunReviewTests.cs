#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.State;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's execution annotations: an author says how a run went - well or badly, tags, a note - recorded with who and
    /// when; the review is listed and read with the run, found by the runs search, refused past its rules saying why, removed when
    /// it says nothing, and deleted with the run.
    /// </summary>
    public sealed class PipelineRunReviewTests : IAsyncLifetime
    {
        private static readonly DateTimeOffset At = new(2026, 10, 7, 12, 30, 0, TimeSpan.Zero);

        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private static PipelineInstance Run(string id, int minute) => new()
        {
            Id = id,
            DefinitionId = "def-orders",
            DefinitionVersionId = "def-orders-1",
            Version = 1,
            Status = PipelineStatus.Finished,
            SubStatus = PipelineSubStatus.Finished,
            Name = "Orders",
            CorrelationId = "c-" + minute,
            CreatedAt = new DateTimeOffset(2026, 10, 7, 7, minute, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 10, 7, 7, minute, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 10, 7, 7, minute, 1, TimeSpan.Zero),
            PipelineState = new PipelineState { Id = id, DefinitionId = "def-orders" },
        };

        [Fact]
        public async Task A_run_is_reviewed_listed_and_found_with_its_review_which_goes_with_it()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            var keyValues = services.GetRequiredService<IKeyValueStore>();
            var tenants = services.GetRequiredService<ITenantAccessor>();
            await instances.AddAsync(Run("run-aaa", 1));
            await instances.AddAsync(Run("run-bbb", 2));
            await instances.AddAsync(Run("run-ccc", 3));
            var review = new SetRunReviewCommandHandler(instances, keyValues, tenants, new User("ada", 7), new Clock());
            var list = new ListRunsQueryHandler(instances, services.GetRequiredService<IPipelineDefinitionStore>(), keyValues, tenants);
            var get = new GetRunQueryHandler(instances, services.GetRequiredService<IPipelineDefinitionStore>(), new Guard(), keyValues, tenants);
            async Task<string[]> Found(string search) =>
                (await list.Handle(new ListRunsQuery(Search: search), CancellationToken.None)).Select(run => run.Id).ToArray();
            Task<PipelineRunReview?> Review(string runId, PipelineRunReview said) => review.Handle(new SetRunReviewCommand(runId, said), CancellationToken.None);

            var kept = await Review("run-aaa", new PipelineRunReview { Rating = " Bad ", Tags = { " late ", "Late", "", "customer" }, Note = "  The invoice came twice.  " });
            Assert.Equal("bad", kept!.Rating);
            Assert.Equal(new[] { "late", "customer" }, kept.Tags);
            Assert.Equal("The invoice came twice.", kept.Note);
            Assert.Equal("ada", kept.By);
            Assert.Equal(At.UtcDateTime, kept.AtUtc);
            await Review("run-bbb", new PipelineRunReview { Rating = "good" });

            // Listed and read with the run; found by a tag, the rating or words of the note.
            var listed = await list.Handle(new ListRunsQuery(), CancellationToken.None);
            Assert.Equal(new[] { "late", "customer" }, listed.Single(run => run.Id == "run-aaa").Review!.Tags);
            Assert.Null(listed.Single(run => run.Id == "run-ccc").Review);
            Assert.Equal("good", (await get.Handle(new GetRunQuery("run-bbb"), CancellationToken.None))!.Review!.Rating);
            Assert.Equal(new[] { "run-aaa" }, await Found("LATE"));
            Assert.Equal(new[] { "run-aaa" }, await Found("bad"));
            Assert.Equal(new[] { "run-bbb" }, await Found("good"));
            Assert.Equal(new[] { "run-aaa" }, await Found("invoice"));

            // Past the rules it is refused saying why; a run not on the site has none; a review saying nothing removes it.
            Assert.Equal("'meh' is not a rating: good, bad, or none.",
                (await Assert.ThrowsAsync<ArgumentException>(() => Review("run-ccc", new PipelineRunReview { Rating = "meh" }))).Message);
            await Assert.ThrowsAsync<ArgumentException>(() => Review("run-ccc", new PipelineRunReview { Tags = Enumerable.Range(1, PipelineRunReviews.MaxTags + 1).Select(i => "t" + i).ToList() }));
            await Assert.ThrowsAsync<ArgumentException>(() => Review("run-ccc", new PipelineRunReview { Note = new string('x', PipelineRunReviews.MaxNoteLength + 1) }));
            Assert.Null(await PipelineRunReviewStore.ReadAsync(keyValues, tenants.TenantId, "run-ccc", CancellationToken.None));
            Assert.Null(await Review("run-zzz", new PipelineRunReview { Rating = "good" }));
            Assert.Null(await PipelineRunReviewStore.ReadAsync(keyValues, tenants.TenantId, "run-zzz", CancellationToken.None));
            var cleared = await Review("run-bbb", new PipelineRunReview { Tags = { " " }, Note = "  " });
            Assert.True(PipelineRunReviews.IsEmpty(cleared));
            Assert.Null(cleared!.By);
            Assert.Null(await PipelineRunReviewStore.ReadAsync(keyValues, tenants.TenantId, "run-bbb", CancellationToken.None));
            Assert.Empty(await Found("good"));

            // A run deleted takes its review with it.
            var deleted = new DeleteRunCommandHandler(services.GetRequiredService<IPipelineInstanceManager>(), keyValues, tenants);
            Assert.True(await deleted.Handle(new DeleteRunCommand("run-aaa"), CancellationToken.None));
            Assert.Null(await PipelineRunReviewStore.ReadAsync(keyValues, tenants.TenantId, "run-aaa", CancellationToken.None));
            Assert.Empty(await Found("late"));
        }

        private sealed class User(string name, int id) : ICurrentUser
        {
            public int? UserId => id;
            public string? UserName => name;
            public bool IsAuthenticated => true;
            public IReadOnlySet<string> Roles => new HashSet<string>();
            public int SiteId => 1;
        }

        private sealed class Clock : ISystemClock
        {
            public DateTimeOffset UtcNow => At;
        }

        private sealed class Guard : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }
    }
}
