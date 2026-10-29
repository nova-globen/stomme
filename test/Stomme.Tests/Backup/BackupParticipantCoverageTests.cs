#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Infrastructure.Backup.Participants;
using Stomme.Modules.HtmlText.Repository;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-81 (gate <c>BackupParticipantCoverageTests</c>) - nothing the installation holds is left out of a snapshot by
    /// accident. On an INSTALLED host (the registrations that really run): every framework store is carried by
    /// <c>framework:databases</c> or is one of the four search stores, which are rebuilt; every storage-blind module
    /// aggregate has exactly one participant or a stated <see cref="BackupExcludedAttribute"/>; every <c>IPortable</c> is
    /// accounted for; participant names are unique. A D42-style FLOOR, never an exact count - and the scanner is validated on
    /// planted types that are uncovered, covered twice, and covered while excluded.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class BackupParticipantCoverageTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        /// <summary>IPortable implementers and where their data travels.</summary>
        private static readonly IReadOnlyDictionary<string, string> PortableCoverage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HtmlTextManager"] = "framework:databases carries HtmlTextAggregate on both providers",
        };

        [Fact]
        public async Task Everything_the_installation_holds_is_carried_or_excluded_for_a_stated_reason()
        {
            // Pipelines composed: a store a module registers only behind its switch (the pipeline activity ledger) is the
            // installation's whenever the switch is on, so the question is asked of a host that has it.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new System.Text.Json.Nodes.JsonObject { ["Pipelines"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                using var scope = app.Services.CreateScope();
                var names = scope.ServiceProvider.GetServices<IBackupParticipant>().Select(participant => participant.Name).ToList();
                output.WriteLine(string.Join(Environment.NewLine, names.OrderBy(name => name, StringComparer.Ordinal)));

                Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
                Assert.Contains(FrameworkDatabasesBackupParticipant.PartName, names);
                Assert.Contains(TenantContentFilesBackupParticipant.PartName, names);
                Assert.DoesNotContain(OpenIddictBackupParticipant.PartName, names);          // registered only while OpenIddict:Enabled is on

                // Framework stores: carried, or the search index.
                var stores = FrameworkStores().ToList();
                Assert.True(stores.Count >= 28, $"only {stores.Count} framework stores were discovered - the scan is broken.");
                var carried = FrameworkDatabasesBackupParticipant.MasterTables.Concat(FrameworkDatabasesBackupParticipant.TenantTables).Append(typeof(HtmlTextAggregate)).ToHashSet();
                var uncovered = stores.Where(store => !carried.Contains(store.Aggregate) && !FrameworkDatabasesBackupParticipant.SearchTables.Contains(store.Aggregate)).Select(store => store.Store.Name).ToList();
                Assert.True(uncovered.Count == 0, "framework stores carried by no participant: " + string.Join(", ", uncovered));

                // Module aggregates: exactly one participant, or excluded with a reason.
                var aggregates = ModuleAggregates().ToList();
                Assert.True(aggregates.Count >= 15, $"only {aggregates.Count} module aggregates were discovered - the scan is broken.");
                var problems = ModuleCoverageProblems(aggregates, names);
                Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

                // The pipeline engine's own stores - every table its context maps and every document type it keeps - are
                // carried by its part, so the next engine table cannot be missed the way all of them were.
                Assert.Contains(Stomme.Modules.Pipelines.Persistence.EngineBackupParticipant.PartName, names);
                // And the site folders its file steps read and write - which no part carried.
                Assert.Contains(Stomme.Modules.Pipelines.Persistence.PipelineFilesBackupParticipant.PartName, names);
                await using (var engine = await scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Stomme.Modules.Pipelines.Persistence.EfCore.EngineDbContext>>().CreateDbContextAsync())
                {
                    var mapped = engine.Model.GetEntityTypes().Select(entity => entity.ClrType).OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
                    Assert.Equal(mapped, Stomme.Modules.Pipelines.Persistence.EngineBackupParticipant.Tables.OrderBy(type => type.FullName, StringComparer.Ordinal).ToList());
                }

                var documents = typeof(Stomme.Modules.Pipelines.Persistence.EngineBackupParticipant).Assembly.GetTypes()
                    .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(Stomme.Modules.Pipelines.Persistence.Raven.EngineDocument)))
                    .OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
                Assert.Equal(documents, Stomme.Modules.Pipelines.Persistence.EngineBackupParticipant.Documents.OrderBy(type => type.FullName, StringComparer.Ordinal).ToList());

                // IPortable: every implementer accounted for, and no stale entry.
                var portables = typeof(HtmlTextAggregate).Assembly.GetTypes()
                    .Where(type => !type.IsAbstract && type.GetInterfaces().Any(i => i.Name == "IPortable")).Select(type => type.Name).ToList();
                Assert.NotEmpty(portables);
                Assert.Empty(portables.Except(PortableCoverage.Keys));
                Assert.Empty(PortableCoverage.Keys.Except(portables));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public void The_scanner_reports_an_uncovered_a_doubly_covered_and_a_covered_yet_excluded_aggregate()
        {
            var names = new List<string>
            {
                "module-aggregate:" + typeof(PlantedTwice).FullName, "module-aggregate:" + typeof(PlantedTwice).FullName,
                "module-aggregate:" + typeof(PlantedExcludedButCovered).FullName,
                "module-aggregate:" + typeof(PlantedCovered).FullName,
            };

            var problems = ModuleCoverageProblems(new[] { typeof(PlantedUncovered), typeof(PlantedTwice), typeof(PlantedExcludedButCovered), typeof(PlantedCovered), typeof(PlantedExcluded) }, names);
            Assert.Contains(problems, problem => problem.Contains(nameof(PlantedUncovered), StringComparison.Ordinal) && problem.Contains("no participant", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.Contains(nameof(PlantedTwice), StringComparison.Ordinal) && problem.Contains("2 participants", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.Contains(nameof(PlantedExcludedButCovered), StringComparison.Ordinal) && problem.Contains("still has a participant", StringComparison.Ordinal));
            Assert.DoesNotContain(problems, problem => problem.Contains(nameof(PlantedCovered) + " ", StringComparison.Ordinal) || problem.Contains(nameof(PlantedExcluded) + " ", StringComparison.Ordinal));
            Assert.Equal(3, problems.Count);
        }

        internal static List<string> ModuleCoverageProblems(IEnumerable<Type> aggregates, IReadOnlyCollection<string> participantNames)
        {
            var problems = new List<string>();
            foreach (var aggregate in aggregates)
            {
                var excluded = aggregate.GetCustomAttribute<BackupExcludedAttribute>() is not null;
                var count = participantNames.Count(name => name == "module-aggregate:" + aggregate.FullName);
                if (excluded && count > 0)
                {
                    problems.Add($"{aggregate.Name} is [BackupExcluded] and still has a participant");
                }
                else if (!excluded && count == 0)
                {
                    problems.Add($"{aggregate.Name} has no participant and no [BackupExcluded(reason)]");
                }
                else if (count > 1)
                {
                    problems.Add($"{aggregate.Name} has {count} participants");
                }
            }

            return problems;
        }

        /// <summary>Every closed framework store: an I...Store interface over IRepository&lt;TAggregate, int&gt;.</summary>
        private static IEnumerable<(Type Store, Type Aggregate)> FrameworkStores()
        {
            foreach (var type in typeof(HtmlTextAggregate).Assembly.GetTypes().Where(type => type.IsInterface && type.Name.EndsWith("Store", StringComparison.Ordinal)))
            {
                var repository = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRepository<,>) && i.GetGenericArguments()[1] == typeof(int));
                if (repository is not null && (type.Namespace == "Stomme.Repository.Stores" || type == typeof(IHtmlTextStore)))
                {
                    yield return (type, repository.GetGenericArguments()[0]);
                }
            }
        }

        /// <summary>Every storage-blind aggregate in the Server and FeatureManagement assemblies - Guid-keyed module aggregates.</summary>
        private static IEnumerable<Type> ModuleAggregates() =>
            new[] { typeof(HtmlTextAggregate).Assembly, typeof(Stomme.FeatureManagement.Domain.FeatureFlag).Assembly }
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => !type.IsAbstract && type.IsClass && IsGuidAggregate(type));

        private static bool IsGuidAggregate(Type type)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
                {
                    return current.GetGenericArguments()[0] == typeof(Guid);
                }
            }

            return false;
        }


        public sealed class PlantedUncovered : AggregateRoot<Guid>
        {
        }

        public sealed class PlantedTwice : AggregateRoot<Guid>
        {
        }

        [BackupExcluded("planted: covered AND excluded must be reported")]
        public sealed class PlantedExcludedButCovered : AggregateRoot<Guid>
        {
        }

        public sealed class PlantedCovered : AggregateRoot<Guid>
        {
        }

        [BackupExcluded("planted: excluded and uncovered is fine")]
        public sealed class PlantedExcluded : AggregateRoot<Guid>
        {
        }
    }
}
