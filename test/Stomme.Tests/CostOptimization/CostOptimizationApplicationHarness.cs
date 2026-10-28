#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.CostOptimization;
using Stomme.CostOptimization.Persistence;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Models;
using Stomme.Modules.CostOptimization.Application;
using Stomme.Modules.CostOptimization.Application.Advisors;
using Stomme.Modules.CostOptimization.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tests.Connectivity;
using Stomme.Tests.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// The CostOptimization application layer over the File store through the module's OWN startup (BL-97 increment 6):
    /// the mediator with the authorization and validation behaviours, a resolved tenant and a site the test can switch,
    /// the predecessor platform's settable clock, and one scripted analyzer standing for the predecessor platform's stub analyzers - it yields the scripted
    /// drafts in order, which is what the command sees of several analyzers in order.
    /// </summary>
    internal sealed class CostOptimizationApplicationHarness : IAsyncDisposable
    {
        public const int SiteId = 1;

        // the predecessor platform's start: 2026-07-01 08:00 UTC.
        public static readonly DateTime Start = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private readonly ServiceProvider _services;
        private readonly RequestSite _site;

        private CostOptimizationApplicationHarness(string root, ServiceProvider services, ManualTimeProvider clock, ScriptedAnalyzer analyzer, RequestSite site)
        {
            _root = root;
            _services = services;
            Clock = clock;
            Analyzer = analyzer;
            _site = site;
        }

        public ManualTimeProvider Clock { get; }

        public ScriptedAnalyzer Analyzer { get; }

        /// <summary>The site the next request resolves; the default is <see cref="SiteId"/>.</summary>
        public int Site
        {
            get => _site.SiteId;
            set => _site.SiteId = value;
        }

        public static Task<CostOptimizationApplicationHarness> StartAsync(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"stomme-costopt-app-{Guid.NewGuid():N}");
            var clock = new ManualTimeProvider(Start);
            var analyzer = new ScriptedAnalyzer();
            var site = new RequestSite { SiteId = SiteId };
            var configuration = new Dictionary<string, string?>
            {
                ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                ["ModuleStore:FilePath"] = root,
            };
            foreach (var setting in settings ?? new Dictionary<string, string?>())
            {
                configuration[setting.Key] = setting.Value;
            }

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = site.SiteId, TenantId = 1, SiteId = site.SiteId, Name = "localhost" } });

            new Stomme.Modules.CostOptimization.Startup.ServerStartup().ConfigureServices(services);
            configure?.Invoke(services); // what the host would provide besides the module (its cache manager, say)

            services.AddSingleton<IRecommendationAnalyzer>(analyzer);
            services.RemoveAll<ICurrentUser>();
            services.AddSingleton<ICurrentUser>(new TestCurrentUser { UserId = 7, SiteId = SiteId });
            services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
            services.AddMediatorBehavior(typeof(AuthorizationBehavior<,>));
            services.AddMediatorBehavior(typeof(ValidationBehavior<,>));
            services.AddScoped<IAccessGuard, AllowAllAccessGuard>();

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return Task.FromResult(new CostOptimizationApplicationHarness(root, provider, clock, analyzer, site));
        }

        public static RecommendationDraft Draft(string key, RecommendationCategory category, decimal saving) =>
            new(category, key, $"Summary for {key}.", new[] { $"Evidence for {key}." }, EstimatedSaving.Of(saving, "USD"));

        public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
        {
            await using var scope = _services.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }

        public Task<T> SendAsync<T>(IRequest<T> request) => InScopeAsync(sp => sp.GetRequiredService<IMediator>().Send(request));

        public Task<AdvisorRunView> RaiseAsync() => SendAsync(new RaiseRecommendationsCommand());

        public Task<IReadOnlyList<OptimizationRecommendationAggregate>> RecommendationsAsync(int siteId = SiteId) =>
            InScopeAsync(sp => sp.GetRequiredService<IOptimizationRecommendationRepository>().ListForSiteAsync(siteId));

        public async ValueTask DisposeAsync()
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
            }
        }

        private sealed class RequestSite
        {
            public int SiteId { get; set; }
        }
    }

    /// <summary>the predecessor platform's <c>StubAnalyzer</c>s in one: the scripted analyzers' drafts, in order.</summary>
    internal sealed class ScriptedAnalyzer : IRecommendationAnalyzer
    {
        private IReadOnlyList<RecommendationDraft[]> _analyzers = Array.Empty<RecommendationDraft[]>();

        /// <summary>Each argument is one analyzer's drafts.</summary>
        public void Script(params RecommendationDraft[][] analyzers) => _analyzers = analyzers;

        public Task<IReadOnlyList<RecommendationDraft>> AnalyzeAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecommendationDraft>>(_analyzers.SelectMany(drafts => drafts).ToList());
    }
}
