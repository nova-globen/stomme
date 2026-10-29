#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.Dashboards;
using Stomme.Landing;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Models;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Landing.Application;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tests.Connectivity;
using Stomme.Tests.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests.Landing
{
    /// <summary>
    /// The Landing requests through the module's real startup and the mediator's authorization behaviour,
    /// on the file store: a visitor sees nothing until a manager makes AND publishes the page, a draft is the managers', a visitor
    /// cannot write, a site's page is its own. Increment 2: a dashboard page shows only a published dashboard of the site the
    /// manager may read, and only to a viewer who may read dashboards - the Dashboards module's own request is scripted here
    /// (its site scoping is the Dashboards suite's), behind the real authorization behaviour.
    /// </summary>
    public sealed class LandingCqrsTests : IAsyncLifetime
    {
        private static readonly DateTime Start = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-landing-app-{Guid.NewGuid():N}");
        private readonly ScriptedGuard _guard = new();
        private readonly RequestSite _site = new() { SiteId = 1 };
        private readonly Dictionary<Guid, DashboardView> _boards = new();
        private readonly ScriptedDesigner _designer = new();
        private ServiceProvider _services = default!;

        public Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                ["ModuleStore:FilePath"] = _root,
            }).Build());
            services.AddSingleton<TimeProvider>(new ManualTimeProvider(Start));
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = _site.SiteId, TenantId = 1, SiteId = _site.SiteId, Name = "localhost" } });
            new Stomme.Modules.Landing.Startup.ServerStartup().ConfigureServices(services);
            services.RemoveAll<ICurrentUser>();
            services.AddSingleton<ICurrentUser>(new TestCurrentUser { UserId = 7, SiteId = 1 });
            services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
            services.AddMediatorBehavior(typeof(AuthorizationBehavior<,>));
            services.AddSingleton<IAccessGuard>(_guard);
            services.AddSingleton(_boards);
            services.AddScoped<IRequestHandler<GetDashboardQuery, DashboardView?>, ScriptedDashboards>();
            services.AddSingleton(_designer);
            services.AddScoped<ILandingPageDesigner>(sp => sp.GetRequiredService<ScriptedDesigner>());
            _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private async Task<T> SendAsync<T>(IRequest<T> request)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
        }

        private static PredefinedLandingContent Content(string headline = "Welcome") =>
            new(headline, "We make things.", new[] { new LandingSectionView("Start", "Read this first.", "Go", "/start") });

        [Fact]
        public async Task A_visitor_sees_nothing_until_a_manager_makes_and_publishes_the_page()
        {
            _guard.Manager = false;
            Assert.Null(await SendAsync(new GetLandingPageQuery()));

            _guard.Manager = true;
            var draft = await SendAsync(new ConfigurePredefinedLandingCommand(Content()));
            Assert.Equal((LandingSource.Predefined, LandingStatus.Draft), (draft.Source, draft.Status));
            Assert.Equal("Welcome", (await SendAsync(new GetLandingPageQuery()))!.Predefined!.Headline); // the manager sees the draft

            _guard.Manager = false;
            Assert.Null(await SendAsync(new GetLandingPageQuery())); // a draft does not exist for a visitor

            _guard.Manager = true;
            var published = await SendAsync(new PublishLandingCommand());
            Assert.Equal(LandingStatus.Published, published.Status);

            _guard.Manager = false;
            var seen = await SendAsync(new GetLandingPageQuery());
            Assert.Equal((published.Id, "Welcome", "/start"), (seen!.Id, seen.Predefined!.Headline, seen.Predefined.Sections[0].LinkUrl));
        }

        [Fact]
        public async Task A_visitor_cannot_make_publish_or_reset_the_page()
        {
            _guard.Manager = false;
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new ConfigurePredefinedLandingCommand(Content())));
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new PublishLandingCommand()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new ResetLandingCommand()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new GetLandingTemplateQuery()));
        }

        [Fact]
        public async Task Making_the_page_twice_edits_the_one_page_and_an_edit_to_a_published_page_is_live()
        {
            _guard.Manager = true;
            var first = await SendAsync(new ConfigurePredefinedLandingCommand(Content("First")));
            await SendAsync(new PublishLandingCommand());
            var second = await SendAsync(new ConfigurePredefinedLandingCommand(Content("Second")));

            Assert.Equal(first.Id, second.Id);
            Assert.Equal((LandingStatus.Published, "Second"), (second.Status, second.Predefined!.Headline));
            _guard.Manager = false;
            Assert.Equal("Second", (await SendAsync(new GetLandingPageQuery()))!.Predefined!.Headline);
        }

        [Fact]
        public async Task Publishing_nothing_is_refused_and_a_reset_offers_the_chooser_again()
        {
            _guard.Manager = true;
            await Assert.ThrowsAsync<KeyNotFoundException>(() => SendAsync(new PublishLandingCommand()));
            await SendAsync(new ConfigurePredefinedLandingCommand(Content()));
            await SendAsync(new PublishLandingCommand());

            await SendAsync(new ResetLandingCommand());

            Assert.Null(await SendAsync(new GetLandingPageQuery()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(new PublishLandingCommand()));
        }

        [Fact]
        public async Task Another_site_of_the_tenant_has_its_own_page()
        {
            _guard.Manager = true;
            await SendAsync(new ConfigurePredefinedLandingCommand(Content("Site one")));
            await SendAsync(new PublishLandingCommand());

            _site.SiteId = 2;
            Assert.Null(await SendAsync(new GetLandingPageQuery()));
            await SendAsync(new ConfigurePredefinedLandingCommand(Content("Site two")));
            Assert.Equal("Site two", (await SendAsync(new GetLandingPageQuery()))!.Predefined!.Headline);

            _site.SiteId = 1;
            Assert.Equal("Site one", (await SendAsync(new GetLandingPageQuery()))!.Predefined!.Headline);
        }

        [Fact]
        public async Task A_manager_starts_from_the_template()
        {
            _guard.Manager = true;
            Assert.Equal(LandingTemplate.Default, await SendAsync(new GetLandingTemplateQuery()));
        }

        [Fact]
        public async Task A_dashboard_page_shows_a_published_dashboard_to_a_viewer_who_may_read_dashboards_only()
        {
            var board = Board(DashboardStatus.Published);
            _guard.Manager = true;
            var made = await SendAsync(new ConfigureDashboardLandingCommand(board.Id));
            Assert.Equal((LandingSource.Dashboard, (Guid?)board.Id, (PredefinedLandingContent?)null, LandingStatus.Draft), (made.Source, made.DashboardId, made.Predefined, made.Status));
            await SendAsync(new PublishLandingCommand());

            _guard.Manager = false;
            Assert.Equal(board.Id, (await SendAsync(new GetLandingPageQuery()))!.DashboardId);

            _guard.DashboardReader = false;
            Assert.Null(await SendAsync(new GetLandingPageQuery())); // the board would show this viewer nothing

            _guard.Manager = true;
            Assert.Equal(board.Id, (await SendAsync(new GetLandingPageQuery()))!.DashboardId); // the manager still sees what is there
        }

        // ---- BL-98 increments 3-4: the AI mode and the edit mode ----------------------------------------------------------

        private const string Designed = "<section data-section=\"hero\"><h1>Welcome</h1></section><section data-section=\"contact\"><p>Write to us.</p></section>";

        [Fact]
        public async Task A_page_generated_from_a_brief_is_proposed_and_nothing_is_stored_and_both_keys_are_needed()
        {
            _guard.Manager = true;
            _designer.Next = LandingDesignResult.Ok(Designed, new[] { "hero", "contact" });

            var draft = await SendAsync(new GenerateLandingDraftCommand("A site for a bakery"));
            Assert.True(draft.Succeeded);
            Assert.Equal(Designed, draft.Html);
            Assert.Equal(new[] { "hero", "contact" }, draft.Sections);
            Assert.Equal("A site for a bakery", _designer.Briefs.Single());
            Assert.Null(await SendAsync(new GetLandingPageQuery())); // proposed, not stored

            await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(new GenerateLandingDraftCommand("  ")));
            _guard.AssistantUser = false;
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new GenerateLandingDraftCommand("A site for a bakery")));
            _guard.AssistantUser = true;
            _guard.Manager = false;
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new GenerateLandingDraftCommand("A site for a bakery")));
            Assert.Single(_designer.Briefs); // neither refusal reached the model
        }

        [Fact]
        public async Task Accepted_html_is_sanitised_again_on_the_server_and_a_page_with_no_section_is_refused()
        {
            _guard.Manager = true;
            var page = await SendAsync(new ConfigureGeneratedLandingCommand(Designed + "<script>alert(1)</script><section data-section=\"x\" onclick=\"steal()\">Click</section>"));

            Assert.Equal(LandingSource.GeneratedHtml, page.Source);
            Assert.DoesNotContain("script", page.GeneratedHtml!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onclick", page.GeneratedHtml!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { "hero", "contact", "x" }, page.Sections);
            Assert.Equal(page.GeneratedHtml, (await SendAsync(new GetLandingPageQuery()))!.GeneratedHtml);

            await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(new ConfigureGeneratedLandingCommand("<script>alert(1)</script>")));
            await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(new ConfigureGeneratedLandingCommand("<p>No section at all.</p>")));
            Assert.Contains("Welcome", (await SendAsync(new GetLandingPageQuery()))!.GeneratedHtml); // the refusals left the page as it was
        }

        [Fact]
        public async Task A_section_revised_is_the_whole_page_proposed_with_that_section_replaced_and_nothing_stored()
        {
            _guard.Manager = true;
            await SendAsync(new ConfigureGeneratedLandingCommand(Designed));
            _designer.Next = LandingDesignResult.Ok("<section data-section=\"hero\"><h1>Fresh bread daily</h1></section>", new[] { "hero" });

            var proposal = await SendAsync(new ProposeLandingSectionCommand("hero", "Say we bake daily"));

            Assert.True(proposal.Succeeded);
            Assert.Contains("Fresh bread daily", proposal.Html);
            Assert.Contains("Write to us.", proposal.Html); // the other section untouched
            Assert.DoesNotContain(">Welcome<", proposal.Html);
            Assert.Equal(("<section data-section=\"hero\"><h1>Welcome</h1></section>", "hero", "Say we bake daily"), _designer.Edits.Single());
            Assert.Contains("Welcome", (await SendAsync(new GetLandingPageQuery()))!.GeneratedHtml); // proposed, not stored

            await Assert.ThrowsAsync<KeyNotFoundException>(() => SendAsync(new ProposeLandingSectionCommand("pricing", "Add prices")));
            await SendAsync(new ConfigurePredefinedLandingCommand(Content()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(new ProposeLandingSectionCommand("hero", "Say we bake daily")));
        }

        [Fact]
        public async Task Only_a_published_dashboard_the_manager_may_read_can_be_the_landing_page()
        {
            var draft = Board(DashboardStatus.Draft);
            var published = Board(DashboardStatus.Published);
            _guard.Manager = true;

            await Assert.ThrowsAsync<KeyNotFoundException>(() => SendAsync(new ConfigureDashboardLandingCommand(Guid.NewGuid())));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(new ConfigureDashboardLandingCommand(draft.Id)));
            await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(new ConfigureDashboardLandingCommand(Guid.Empty)));

            _guard.DashboardReader = false;
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new ConfigureDashboardLandingCommand(published.Id)));

            _guard.Manager = false;
            _guard.DashboardReader = true;
            await Assert.ThrowsAsync<AccessDeniedException>(() => SendAsync(new ConfigureDashboardLandingCommand(published.Id)));

            _guard.Manager = true;
            Assert.Null(await SendAsync(new GetLandingPageQuery())); // nothing refused left a page behind
        }

        [Fact]
        public async Task A_page_moves_between_the_template_and_a_dashboard_as_one_page()
        {
            var board = Board(DashboardStatus.Published);
            _guard.Manager = true;
            var predefined = await SendAsync(new ConfigurePredefinedLandingCommand(Content()));
            await SendAsync(new PublishLandingCommand());

            var dashboard = await SendAsync(new ConfigureDashboardLandingCommand(board.Id));
            Assert.Equal((predefined.Id, LandingSource.Dashboard, LandingStatus.Published), (dashboard.Id, dashboard.Source, dashboard.Status));

            var back = await SendAsync(new ConfigurePredefinedLandingCommand(Content("Back")));
            Assert.Equal((LandingSource.Predefined, (Guid?)null, "Back"), (back.Source, back.DashboardId, back.Predefined!.Headline));
        }

        private DashboardView Board(DashboardStatus status)
        {
            var board = new DashboardView(Guid.NewGuid(), $"Board {_boards.Count + 1}", status, 12, 40, Start, Start, Array.Empty<WidgetView>());
            _boards[board.Id] = board;
            return board;
        }

        // The Dashboards module's own request, scripted: the landing handler sends it through the mediator, so the real
        // authorization behaviour decides its [Requires(dashboards:view)].
        public sealed class ScriptedDashboards(Dictionary<Guid, DashboardView> boards) : IQueryHandler<GetDashboardQuery, DashboardView?>
        {
            public Task<DashboardView?> Handle(GetDashboardQuery request, CancellationToken ct) =>
                Task.FromResult(boards.TryGetValue(request.Id, out var board) ? board : null);
        }

        private sealed class RequestSite
        {
            public int SiteId { get; set; }
        }

        // Every visitor holds landing:view; landing:manage is the administrators'; dashboards:view is scripted separately.
        // Decided from the request's [Requires], as the host's guard does.
        /// <summary>The designer as a script: what it returns next, and every brief and edit it was asked for.</summary>
        private sealed class ScriptedDesigner : ILandingPageDesigner
        {
            public LandingDesignResult Next { get; set; } = LandingDesignResult.Failed(new[] { "nothing scripted" });

            public List<string> Briefs { get; } = new();

            public List<(string SectionHtml, string Section, string Instruction)> Edits { get; } = new();

            public Task<LandingDesignResult> GenerateAsync(string brief, CancellationToken cancellationToken = default)
            {
                Briefs.Add(brief);
                return Task.FromResult(Next);
            }

            public Task<LandingDesignResult> EditSectionAsync(string sectionHtml, string section, string instruction, CancellationToken cancellationToken = default)
            {
                Edits.Add((sectionHtml, section, instruction));
                return Task.FromResult(Next);
            }
        }

        private sealed class ScriptedGuard : IAccessGuard
        {
            public bool Manager { get; set; }

            public bool DashboardReader { get; set; } = true;

            public bool AssistantUser { get; set; } = true;

            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) =>
                Task.FromResult(permission == LandingPermissions.Manage ? Manager
                    : permission == DashboardPermissions.View ? DashboardReader
                    : permission == Stomme.AiAssistant.AssistantPermissions.Use ? AssistantUser
                    : true);

            public async Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default)
            {
                foreach (var requires in target.GetCustomAttributes<RequiresAttribute>())
                {
                    if (!await IsAllowedAsync(requires.Feature, requires.Permission, ct))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
