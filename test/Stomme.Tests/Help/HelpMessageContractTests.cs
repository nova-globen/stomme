#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Help;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.ContextualHelp.Application;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Stomme.Modules.ContextualHelp.Startup;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// The help module's mediator surface, reflectively (BL-84, D103 item 8 - the spec's
    /// <c>HelpSeedIsNotAMediatorMessageTests</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every request carries <c>[Requires]</c> with a help key</b> - the authorization behaviour decides nothing for a
    /// request without one - and <b>every request has a registered handler while help is on.</b> The second one was
    /// found by DRIVING: the handlers are registered explicitly (never <c>AddMediator()</c>, BL-78 F13), none was, and
    /// every <c>api/Help</c> call answered an empty 500. Nothing short of an HTTP call had noticed.
    /// </para>
    /// <para>
    /// <b>The seed path is not a message.</b> The reconciler is not a request and has no handler, so no caller - with
    /// any key - can make a boot's seeding happen or re-happen.
    /// </para>
    /// </remarks>
    public class HelpMessageContractTests
    {
        private readonly ITestOutputHelper _output;

        public HelpMessageContractTests(ITestOutputHelper output) => _output = output;

        private static IReadOnlyList<(Type Request, Type Response)> Requests() =>
            typeof(HelpTopicReconciler).Assembly.GetTypes()
                .Where(type => type.Namespace?.StartsWith("Stomme.Modules.ContextualHelp", StringComparison.Ordinal) == true && !type.IsAbstract && !type.IsInterface)
                .SelectMany(type => type.GetInterfaces()
                    .Where(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IRequest<>))
                    .Select(item => (type, item.GetGenericArguments()[0])))
                .ToList();

        [Fact]
        public void Every_help_request_carries_Requires_with_a_HELP_key()
        {
            var requests = Requests();
            _output.WriteLine(string.Join("\n", requests.Select(item => item.Request.Name)));

            // A floor, not a count: the ten messages this module ships today.
            Assert.True(requests.Count >= 10, $"only {requests.Count} request types found - the sweep is broken");

            foreach (var (request, _) in requests)
            {
                var requires = request.GetCustomAttributes<RequiresAttribute>().ToList();
                Assert.True(requires.Count > 0, $"{request.Name} carries no [Requires] - the authorization behaviour would decide nothing for it");
                Assert.All(requires, attribute => Assert.Contains(attribute.Permission, HelpPermissions.All));
            }
        }

        [Fact]
        public void The_writes_need_author_or_manage_and_ONLY_the_two_reads_are_help_view()
        {
            // Refutation 15 at the message level: view is the ONLY key an anonymous principal holds, so the set of
            // messages it opens must be exactly the two published-only reads.
            var viewGated = Requests()
                .Where(item => item.Request.GetCustomAttributes<RequiresAttribute>().Any(attribute => attribute.Permission == HelpPermissions.View))
                .Select(item => item.Request)
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { typeof(ListPublishedHelpTopicsQuery), typeof(ResolveHelpTopicQuery) }, viewGated);
        }

        [Fact]
        public void Every_help_request_has_a_handler_while_help_is_ON_and_none_while_it_is_OFF()
        {
            using var on = Compose(enabled: true);
            using var scope = on.CreateScope();

            var missing = new List<string>();
            foreach (var (request, response) in Requests())
            {
                var handlerType = typeof(IRequestHandler<,>).MakeGenericType(request, response);
                try
                {
                    if (scope.ServiceProvider.GetService(handlerType) is null)
                    {
                        missing.Add(request.Name + " (not registered)");
                    }
                }
                catch (Exception ex)
                {
                    missing.Add($"{request.Name} ({ex.GetType().Name}: {ex.Message})");
                }
            }

            Assert.True(missing.Count == 0, "help requests with no resolvable handler while Help:Enabled is on:\n" + string.Join("\n", missing));

            // Off means absent: no handler is composed, so nothing can dispatch into services that do not exist.
            using var off = Compose(enabled: false);
            using var offScope = off.CreateScope();
            Assert.All(Requests(), item =>
                Assert.Null(offScope.ServiceProvider.GetService(typeof(IRequestHandler<,>).MakeGenericType(item.Request, item.Response))));
        }

        [Fact]
        public void The_seed_path_is_NOT_a_mediator_message()
        {
            Assert.DoesNotContain(typeof(HelpTopicReconciler).GetInterfaces(),
                item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IRequest<>));
            Assert.DoesNotContain(typeof(HelpTopicReconciler).GetInterfaces(),
                item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IRequestHandler<,>));
            Assert.DoesNotContain(Requests(), item => item.Request.Name.Contains("Seed", StringComparison.Ordinal)
                                                      && !item.Request.Name.Contains("Reset", StringComparison.Ordinal));
        }

        /// <summary>The module's own composition, with the dependencies the host supplies stubbed only where the handlers need them to construct.</summary>
        private static ServiceProvider Compose(bool enabled)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = "Data Source=:memory:",
                    [HelpSettings.EnabledKey] = enabled ? "true" : "false",
                })
                .Build());
            services.AddScoped<SiteState>();
            services.AddSingleton<ICacheManager>(new NoCache());
            services.AddScoped<Stomme.Repository.ILanguageRepository, NoLanguages>();
            services.AddSingleton<Stomme.FeatureManagement.Application.IFeatureConfigurationSource>(Stomme.FeatureManagement.Application.NullFeatureConfigurationSource.Instance);
            new ServerStartup().ConfigureServices(services);
            return services.BuildServiceProvider();
        }

        private sealed class NoLanguages : Stomme.Repository.ILanguageRepository
        {
            public System.Threading.Tasks.Task<IEnumerable<Stomme.Models.Language>> GetLanguagesAsync(int siteId) =>
                System.Threading.Tasks.Task.FromResult<IEnumerable<Stomme.Models.Language>>(Array.Empty<Stomme.Models.Language>());
            public System.Threading.Tasks.Task<Stomme.Models.Language> AddLanguageAsync(Stomme.Models.Language language) => throw new NotSupportedException();
            public System.Threading.Tasks.Task UpdateLanguageAsync(Stomme.Models.Language language) => throw new NotSupportedException();
            public System.Threading.Tasks.Task<Stomme.Models.Language> GetLanguageAsync(int languageId) => throw new NotSupportedException();
            public System.Threading.Tasks.Task DeleteLanguageAsync(int languageId) => throw new NotSupportedException();
        }

        private sealed class NoCache : ICacheManager
        {
            public System.Threading.Tasks.Task<T> GetCacheAsync<T>(string key, Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> factory) => factory(default);
            public System.Threading.Tasks.Task<T> GetCacheAsync<T>(string key, Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> factory, TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration) => factory(default);
            public System.Threading.Tasks.Task RemoveCacheAsync(string key) => System.Threading.Tasks.Task.CompletedTask;
            public System.Threading.Tasks.Task<T> GetCacheAsync<T>(Stomme.Models.Alias alias, string key, Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> factory) => factory(default);
            public System.Threading.Tasks.Task<T> GetCacheAsync<T>(Stomme.Models.Alias alias, string key, Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> factory, TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration) => factory(default);
            public System.Threading.Tasks.Task RemoveCacheAsync(Stomme.Models.Alias alias, string key) => System.Threading.Tasks.Task.CompletedTask;
            public System.Threading.Tasks.Task ClearAllAsync() => System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
