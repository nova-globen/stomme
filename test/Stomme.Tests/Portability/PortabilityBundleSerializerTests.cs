#nullable enable
using System;
using System.Linq;
using Stomme.Modules.Portability.Application.Format;
using Stomme.Portability;
using Xunit;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// The package format. Eight serializer facts, held on format 1.1, plus what
    /// 1.1 adds: a 1.0 document stays readable, a newer minor is refused, and the ordering is deterministic whatever
    /// order the contributors produced.
    /// </summary>
    public class PortabilityBundleSerializerTests
    {
        private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        private readonly PortabilityBundleSerializer _serializer = new();

        private static PortableEntity Entity(string type, string id, int schema = 1, string payload = "{}", params PortableEntityRef[] dependencies) =>
            new(new PortableEntityRef(type, id), $"{type} {id}", schema, payload, false, dependencies);

        private static PortabilityBundleDocument AConfigDocument() => PortabilityBundleDocument.Create(
            PortabilityPackageKinds.ConfigTransfer, "1:1", null, new PortabilityBundleProvenance(7, Now, "dev-instance"),
            new[]
            {
                Entity("pipeline", "pipeline-1", payload: "{\"name\":\"nightly\"}", dependencies: new PortableEntityRef("connection-definition", "conn-1")),
                Entity("connection-definition", "conn-1", schema: 2, payload: "{\"host\":\"db\"}"),
            });

        [Fact]
        public void Document_round_trips_through_json()
        {
            var result = _serializer.Read(_serializer.Serialize(AConfigDocument()));

            Assert.True(result.Success);
            Assert.Empty(result.Report);
            var read = result.Document!;
            Assert.Equal(PortabilityPackageKinds.ConfigTransfer, read.Kind);
            Assert.Equal("1:1", read.SiteRef);
            Assert.Null(read.Subject);
            Assert.Equal(BundleFormatVersion.Current, read.Manifest.Format);
            Assert.Equal((int?)7, read.Provenance.ExportedByUserId);
            Assert.Equal(Now, read.Provenance.ExportedAtUtc);
            Assert.Equal(DateTimeKind.Utc, read.Provenance.ExportedAtUtc.Kind);
            Assert.Equal(2, read.Manifest.Entries.Count);
            Assert.Equal(2, read.Entities.Count);

            // Deterministic ordering: connection-definition sorts before pipeline.
            Assert.Equal("connection-definition", read.Entities[0].EntityType);
            Assert.Equal("pipeline", read.Entities[1].EntityType);
            Assert.Equal("{\"host\":\"db\"}", read.Entities[0].Payload);

            // What 1.0 dropped on the wire, 1.1 carries.
            Assert.Equal("pipeline pipeline-1", read.Entities[1].DisplayName);
            Assert.Equal(new PortabilityBundleRef("connection-definition", "conn-1"), Assert.Single(read.Entities[1].Dependencies!));
        }

        [Fact]
        public void Serialize_is_deterministic_for_the_same_package()
        {
            var document = AConfigDocument();

            Assert.Equal(_serializer.Serialize(document), _serializer.Serialize(document));
        }

        [Fact]
        public void The_bytes_do_not_depend_on_the_order_the_contributors_produced()
        {
            var provenance = new PortabilityBundleProvenance(7, Now, null);
            var a = Entity("b-type", "2", dependencies: new[] { new PortableEntityRef("a-type", "9"), new PortableEntityRef("a-type", "1") });
            var b = Entity("a-type", "9");
            var c = Entity("a-type", "1");

            var one = _serializer.Serialize(PortabilityBundleDocument.Create(PortabilityPackageKinds.ConfigTransfer, null, null, provenance, new[] { a, b, c }));
            var two = _serializer.Serialize(PortabilityBundleDocument.Create(PortabilityPackageKinds.ConfigTransfer, null, null, provenance,
                new[] { c, Entity("b-type", "2", dependencies: new[] { new PortableEntityRef("a-type", "1"), new PortableEntityRef("a-type", "9") }), b }));

            Assert.Equal(one, two);
        }

        [Fact]
        public void Subject_data_bundle_carries_the_subject()
        {
            var document = PortabilityBundleDocument.Create(
                PortabilityPackageKinds.SubjectData, "1:1", new PortabilityBundleSubject("42", "user"), new PortabilityBundleProvenance(7, Now, null),
                new[] { Entity("user-account", "42") });

            var read = _serializer.Read(_serializer.Serialize(document)).Document!;

            Assert.Equal(PortabilityPackageKinds.SubjectData, read.Kind);
            Assert.NotNull(read.Subject);
            Assert.Equal("42", read.Subject!.SubjectId);
            Assert.Equal("user", read.Subject.SubjectType);
        }

        [Fact]
        public void Read_rejects_an_incompatible_major_version_with_a_report()
        {
            var json = _serializer.Serialize(AConfigDocument() with { Manifest = new PortabilityBundleManifest(new BundleFormatVersion(2, 0), Array.Empty<PortabilityBundleManifestEntry>()) });

            var result = _serializer.Read(json);

            Assert.False(result.Success);
            Assert.Null(result.Document);
            Assert.Contains(result.Report, line => line.Contains("not readable", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Read_refuses_a_NEWER_minor_of_the_same_major()
        {
            var json = _serializer.Serialize(AConfigDocument() with { Manifest = new PortabilityBundleManifest(new BundleFormatVersion(1, BundleFormatVersion.Current.Minor + 1), Array.Empty<PortabilityBundleManifestEntry>()) });

            Assert.False(_serializer.Read(json).Success);
        }

        [Fact]
        public void Read_accepts_an_older_compatible_minor()
        {
            var json = _serializer.Serialize(AConfigDocument() with { Manifest = new PortabilityBundleManifest(new BundleFormatVersion(1, 0), Array.Empty<PortabilityBundleManifestEntry>()) });

            Assert.True(_serializer.Read(json).Success);
        }

        /// <summary>A document exactly as format 1.0 wrote it: a Guid tenant, a Guid exporter, <c>exportedAt</c>, no names or dependencies.</summary>
        [Fact]
        public void A_Predecessor_1_0_document_is_readable()
        {
            const string stommeBit = """
            {
              "kind": "ConfigTransfer",
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "provenance": { "exportedBy": "22222222-2222-2222-2222-222222222222", "exportedAt": "2026-06-30T12:00:00+00:00", "sourceInstance": "dev" },
              "manifest": { "format": { "major": 1, "minor": 0 }, "entries": [ { "entityType": "pipeline", "schemaVersion": 1, "count": 1 } ] },
              "entities": [ { "entityType": "pipeline", "entityId": "p-1", "schemaVersion": 1, "payload": "{}", "containsSecrets": false } ]
            }
            """;

            var result = _serializer.Read(stommeBit);

            Assert.True(result.Success, string.Join("; ", result.Report));
            var read = result.Document!;
            Assert.Null(read.Provenance.ExportedByUserId);                       // a Guid exporter is "unknown" here
            Assert.Equal(new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc), read.Provenance.ExportedAtUtc);
            var entity = Assert.Single(read.Entities);
            Assert.Null(entity.DisplayName);
            Assert.Null(entity.Dependencies);
        }

        [Fact]
        public void Read_rejects_malformed_json_with_a_report()
        {
            var result = _serializer.Read("{ not json");

            Assert.False(result.Success);
            Assert.NotEmpty(result.Report);
        }

        [Fact]
        public void Read_rejects_empty_input()
        {
            Assert.False(_serializer.Read("").Success);
            Assert.False(_serializer.Read("   ").Success);
        }

        [Fact]
        public void Read_rejects_a_document_with_no_format_version()
        {
            Assert.False(_serializer.Read("""{ "kind": "ConfigTransfer", "entities": [] }""").Success);
        }

        [Fact]
        public void The_manifest_counts_each_type_and_schema_version()
        {
            var document = PortabilityBundleDocument.Create(PortabilityPackageKinds.ConfigTransfer, null, null, new PortabilityBundleProvenance(1, Now, null),
                new[] { Entity("t", "1"), Entity("t", "2"), Entity("t", "3", schema: 2), Entity("u", "1") });

            Assert.Equal(
                new[] { ("t", 1, 2), ("t", 2, 1), ("u", 1, 1) },
                document.Manifest.Entries.Select(line => (line.EntityType, line.SchemaVersion, line.Count)));
        }
    }
}
