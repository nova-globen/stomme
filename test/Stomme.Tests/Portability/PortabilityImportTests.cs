#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Portability.Application;
using Stomme.Modules.Portability.Application.Format;
using Stomme.Portability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.Portability
{
    /// <summary>A contributor that records every call, so a test can prove an import made NONE.</summary>
    internal sealed class RecordingContributor(string entityType, params int[] readable) : IPortableEntityContributor
    {
        public List<string> Calls { get; } = new();

        public Func<PortableEntity, string?> Refuse { get; set; } = _ => null;

        public Func<PortableEntity, EntityImportOutcome> Outcome { get; set; } = entity => new EntityImportOutcome(EntityImportResult.Imported, "t-" + entity.Ref.EntityId);

        public List<PortableEntity> Source { get; } = new();

        public string EntityType => entityType;

        public int CurrentSchemaVersion => ReadableSchemaVersions.Max();

        public IReadOnlyCollection<int> ReadableSchemaVersions => readable.Length == 0 ? new[] { 1 } : readable;

        public Task<IReadOnlyList<PortableEntity>> ExportAsync(PortabilityScope scope, CancellationToken cancellationToken = default)
        {
            Calls.Add($"export {string.Join(",", scope.EntityIds ?? new[] { "*" })}");
            return Task.FromResult<IReadOnlyList<PortableEntity>>(Source.Where(entity => scope.EntityIds is null || scope.EntityIds.Contains(entity.Ref.EntityId)).ToList());
        }

        public Task<string?> ValidateImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"validate {entity.Ref}");
            return Task.FromResult(Refuse(entity));
        }

        public Task<EntityImportOutcome> ImportAsync(PortableEntity entity, ImportContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"import {entity.Ref}");
            return Task.FromResult(Outcome(entity));
        }
    }

    internal sealed class PortabilityLogCapture<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// BL-82 (D107 item 3) - validate EVERYTHING, then change anything. Each way a package can be wrong is refused with a
    /// reason and ZERO import calls on any contributor - the predecessor platform ran its providers in document order and validated
    /// afterwards, so a bad entity left side effects with no report.
    /// </summary>
    public class PortabilityImportValidatesBeforeSideEffectsTests
    {
        private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        private const long Limit = 10 * 1024 * 1024;

        private readonly RecordingContributor _pages = new("page");
        private readonly RecordingContributor _modules = new("module-content", 1, 2);
        private readonly PortabilityBundleSerializer _serializer = new();

        private PortabilityImporter Importer() =>
            new(new PortabilityContributorRegistry(new IPortableEntityContributor[] { _pages, _modules }, NullLogger<PortabilityContributorRegistry>.Instance), _serializer);

        private static ImportContext Context() => new(1, ConflictStrategy.Skip, new PortableIdMap());

        private static PortableEntity Entity(string type, string id, int schema = 1, string payload = "{}", bool secret = false, params PortableEntityRef[] dependencies) =>
            new(new PortableEntityRef(type, id), id, schema, payload, secret, dependencies);

        private string Package(params PortableEntity[] entities) =>
            _serializer.Serialize(PortabilityBundleDocument.Create(PortabilityPackageKinds.ConfigTransfer, "1:1", null, new PortabilityBundleProvenance(1, Now, null), entities));

        /// <summary>Edits the serialised document - how a hand-made or tampered package reaches the importer.</summary>
        private static string Edit(string json, Action<JsonObject> edit)
        {
            var node = JsonNode.Parse(json)!.AsObject();
            edit(node);
            return node.ToJsonString();
        }

        private void AssertNoSideEffect(ImportReport report, string expected)
        {
            Assert.False(report.Accepted);
            Assert.Contains(report.Refusals, refusal => refusal.Contains(expected, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(report.Lines);
            Assert.DoesNotContain(_pages.Calls.Concat(_modules.Calls), call => call.StartsWith("import", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_valid_package_is_imported()
        {
            var report = await Importer().ImportAsync(Package(Entity("page", "1"), Entity("module-content", "5", schema: 2)), Limit, Context());

            Assert.True(report.Accepted, string.Join("; ", report.Refusals));
            Assert.Equal(2, report.Lines.Count);
            Assert.All(report.Lines, line => Assert.Equal(EntityImportResult.Imported, line.Result));
        }

        [Fact]
        public async Task An_entity_marked_as_holding_a_secret_refuses_the_package() =>
            AssertNoSideEffect(await Importer().ImportAsync(Package(Entity("page", "1"), Entity("page", "2", secret: true)), Limit, Context()), "secret");

        [Fact]
        public async Task A_subject_data_package_is_refused_on_the_config_path()
        {
            var json = _serializer.Serialize(PortabilityBundleDocument.Create(
                PortabilityPackageKinds.SubjectData, "1:1", new PortabilityBundleSubject("42", "user"), new PortabilityBundleProvenance(1, Now, null), new[] { Entity("page", "1") }));

            AssertNoSideEffect(await Importer().ImportAsync(json, Limit, Context()), "kind");
        }

        [Fact]
        public async Task An_unknown_kind_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Edit(Package(Entity("page", "1")), node => node["kind"] = "Mystery"), Limit, Context()), "kind");

        [Fact]
        public async Task A_schema_version_below_one_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Edit(Package(Entity("page", "1"), Entity("page", "2")), node =>
            {
                node["entities"]![1]!["schemaVersion"] = 0;
                node["manifest"]!["entries"] = new JsonArray(
                    new JsonObject { ["entityType"] = "page", ["schemaVersion"] = 0, ["count"] = 1 },
                    new JsonObject { ["entityType"] = "page", ["schemaVersion"] = 1, ["count"] = 1 });
            }), Limit, Context()), "schema version 0");

        [Fact]
        public async Task An_unreadable_schema_version_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Package(Entity("page", "1"), Entity("page", "2", schema: 3)), Limit, Context()), "not readable");

        [Fact]
        public async Task A_blank_id_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Edit(Package(Entity("page", "1"), Entity("page", "2")), node => node["entities"]![1]!["entityId"] = " "), Limit, Context()), "blank");

        [Fact]
        public async Task A_type_nothing_imports_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Package(Entity("page", "1"), Entity("pipeline", "9")), Limit, Context()), "nothing on this installation");

        [Fact]
        public async Task A_manifest_that_does_not_match_the_entities_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Edit(Package(Entity("page", "1"), Entity("page", "2")), node => node["manifest"]!["entries"]![0]!["count"] = 5), Limit, Context()), "manifest");

        [Fact]
        public async Task A_dependency_cycle_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Package(
                Entity("page", "1", dependencies: new PortableEntityRef("page", "2")),
                Entity("page", "2", dependencies: new PortableEntityRef("page", "1"))), Limit, Context()), "cycle");

        [Fact]
        public async Task A_dependency_the_package_does_not_carry_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Package(Entity("page", "1", dependencies: new PortableEntityRef("page", "404"))), Limit, Context()), "does not carry");

        [Fact]
        public async Task An_entity_that_appears_twice_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Edit(Package(Entity("page", "1"), Entity("page", "2")), node => node["entities"]![1]!["entityId"] = "1"), Limit, Context()), "twice");

        [Fact]
        public async Task A_payload_that_is_not_JSON_is_refused() =>
            AssertNoSideEffect(await Importer().ImportAsync(Edit(Package(Entity("page", "1")), node => node["entities"]![0]!["payload"] = "<script>"), Limit, Context()), "not JSON");

        [Fact]
        public async Task A_package_over_the_size_cap_is_refused_before_it_is_read()
        {
            var json = Package(Entity("page", "1", payload: "{\"x\":\"" + new string('a', 2048) + "\"}"));

            AssertNoSideEffect(await Importer().ImportAsync(json, maxBytes: 1024, Context()), "limit");
        }

        [Fact]
        public async Task A_contributor_refusal_of_the_LAST_entity_stops_the_first_being_imported()
        {
            _modules.Refuse = entity => entity.Ref.EntityId == "5" ? "the module is on another site" : null;

            var report = await Importer().ImportAsync(Package(Entity("page", "1"), Entity("module-content", "5")), Limit, Context());

            AssertNoSideEffect(report, "another site");
            Assert.Contains("validate page:1", _pages.Calls);                 // it WAS validated
        }

        [Fact]
        public async Task A_contributor_that_throws_on_import_is_a_Failed_line_with_the_exception_type_not_its_message()
        {
            _pages.Outcome = _ => throw new InvalidOperationException("Server=db;Password=hunter2");

            var report = await Importer().ImportAsync(Package(Entity("page", "1"), Entity("module-content", "5")), Limit, Context());

            Assert.True(report.Accepted);
            var failed = Assert.Single(report.Lines, line => line.EntityType == "page");
            Assert.Equal(EntityImportResult.Failed, failed.Result);
            Assert.Equal(nameof(InvalidOperationException), failed.Reason);
            Assert.Equal(EntityImportResult.Imported, Assert.Single(report.Lines, line => line.EntityType == "module-content").Result);
        }
    }

    /// <summary>BL-82 - dependencies are imported first, and a dependant sees what its dependency became on this site.</summary>
    public class PortabilityImportDependencyOrderTests
    {
        private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task Dependencies_are_imported_first_and_their_target_ids_are_mapped()
        {
            var pages = new RecordingContributor("page");
            var modules = new RecordingContributor("module-content");
            string? resolvedParent = null;
            var map = new PortableIdMap();
            modules.Outcome = entity =>
            {
                resolvedParent = map.Resolve(entity.Dependencies.Single());
                return new EntityImportOutcome(EntityImportResult.Imported, "m-" + entity.Ref.EntityId);
            };

            var serializer = new PortabilityBundleSerializer();
            // Document order puts the dependant FIRST ("module-content" sorts before "page").
            var json = serializer.Serialize(PortabilityBundleDocument.Create(PortabilityPackageKinds.ConfigTransfer, null, null, new PortabilityBundleProvenance(1, Now, null), new[]
            {
                new PortableEntity(new PortableEntityRef("module-content", "5"), "html", 1, "{}", false, new[] { new PortableEntityRef("page", "1") }),
                new PortableEntity(new PortableEntityRef("page", "1"), "home", 1, "{}"),
            }));
            var importer = new PortabilityImporter(new PortabilityContributorRegistry(new IPortableEntityContributor[] { pages, modules }, NullLogger<PortabilityContributorRegistry>.Instance), serializer);

            var report = await importer.ImportAsync(json, 1_000_000, new ImportContext(2, ConflictStrategy.Skip, map));

            Assert.True(report.Accepted, string.Join("; ", report.Refusals));
            Assert.Equal(new[] { "page", "module-content" }, report.Lines.Select(line => line.EntityType));
            Assert.Equal("t-1", resolvedParent);
        }
    }

    /// <summary>BL-82 (D107 item 16, the BL-105 posture) - a type claimed twice is claimed by nobody, loudly, and the host goes on.</summary>
    public class PortabilityContributorConflictTests
    {
        [Fact]
        public void A_type_claimed_by_two_contributors_is_excluded_and_logged_at_Error()
        {
            var logger = new PortabilityLogCapture<PortabilityContributorRegistry>();
            var registry = new PortabilityContributorRegistry(new IPortableEntityContributor[]
            {
                new RecordingContributor("page"), new RecordingContributor("page"), new RecordingContributor("profile"),
            }, logger);

            Assert.Null(registry.Find("page"));
            Assert.NotNull(registry.Find("profile"));
            Assert.Equal(new[] { "page" }, registry.Excluded);
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("page", StringComparison.Ordinal));
        }
    }

    /// <summary>BL-82 - an export is the requested entities plus their dependency closure, or a refusal - never a broken package.</summary>
    public class PortabilityExporterTests
    {
        private static PortabilityExporter Exporter(params IPortableEntityContributor[] contributors) =>
            new(new PortabilityContributorRegistry(contributors, NullLogger<PortabilityContributorRegistry>.Instance));

        [Fact]
        public async Task The_closure_of_dependencies_is_collected_breadth_first()
        {
            var pages = new RecordingContributor("page");
            var modules = new RecordingContributor("module-content");
            var profiles = new RecordingContributor("profile");
            pages.Source.Add(new PortableEntity(new PortableEntityRef("page", "1"), "home", 1, "{}", false, new[] { new PortableEntityRef("profile", "p") }));
            pages.Source.Add(new PortableEntity(new PortableEntityRef("page", "2"), "other", 1, "{}"));
            modules.Source.Add(new PortableEntity(new PortableEntityRef("module-content", "5"), "html", 1, "{}", false, new[] { new PortableEntityRef("page", "1") }));
            profiles.Source.Add(new PortableEntity(new PortableEntityRef("profile", "p"), "p", 1, "{}"));

            var entities = await Exporter(pages, modules, profiles).ExportConfigAsync(1, new Dictionary<string, IReadOnlyCollection<string>?> { ["module-content"] = new[] { "5" } });

            Assert.Equal(new[] { "module-content:5", "page:1", "profile:p" }, entities.Select(entity => entity.Ref.ToString()).OrderBy(s => s, StringComparer.Ordinal));
            Assert.DoesNotContain(entities, entity => entity.Ref.EntityId == "2");
        }

        [Fact]
        public async Task A_dependency_nobody_returns_fails_the_build() =>
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                var modules = new RecordingContributor("module-content");
                modules.Source.Add(new PortableEntity(new PortableEntityRef("module-content", "5"), "html", 1, "{}", false, new[] { new PortableEntityRef("page", "404") }));
                await Exporter(modules, new RecordingContributor("page")).ExportConfigAsync(1, new Dictionary<string, IReadOnlyCollection<string>?> { ["module-content"] = null });
            });

        [Fact]
        public async Task A_secret_in_a_configuration_export_fails_the_build() =>
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                var pages = new RecordingContributor("page");
                pages.Source.Add(new PortableEntity(new PortableEntityRef("page", "1"), "home", 1, "{}", containsSecrets: true));
                await Exporter(pages).ExportConfigAsync(1, new Dictionary<string, IReadOnlyCollection<string>?> { ["page"] = null });
            });

        [Fact]
        public async Task A_subject_export_deduplicates_by_type_and_id()
        {
            var one = new FixedSubjectContributor("user-account", new PortableEntity(new PortableEntityRef("user-account", "42"), "ada", 1, "{}"));
            var two = new FixedSubjectContributor("user-account-again", new PortableEntity(new PortableEntityRef("user-account", "42"), "ada", 1, "{}"));

            var entities = await PortabilityExporter.ExportSubjectAsync(new ISubjectDataContributor[] { one, two }, new SubjectScope(1, 42));

            Assert.Single(entities);
        }

        private sealed class FixedSubjectContributor(string type, params PortableEntity[] entities) : ISubjectDataContributor
        {
            public string SubjectDataType => type;

            public Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PortableEntity>>(entities);
        }
    }
}
