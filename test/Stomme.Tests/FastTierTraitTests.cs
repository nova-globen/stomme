#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The FAST TIER: <c>--filter "Category!=ExternalDatabase"</c> leaves out every test class that reaches
    /// PostgreSQL or RavenDB, for iterating in a few minutes. This gate keeps the trait true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fast tier is NOT the commit gate</b> - <c>dotnet test Stomme.slnx</c>, unfiltered, is. A tier that leaves
    /// out both real providers cannot see a provider divergence, the renderer deadlock or a parity break, which is
    /// most of what has actually gone wrong here. It exists so that an edit-and-run loop is not twenty minutes long.
    /// </para>
    /// <para>
    /// The trait is per CLASS, because xunit 2 has no per-row trait: a theory with a SQLite row and a RavenDB row leaves
    /// the fast tier whole. That is the price, and it is the right way round - a fast tier that is a little too small
    /// is an inconvenience, one that silently reaches a shared server is not fast and not honest.
    /// </para>
    /// <para>
    /// <b>What a source scan cannot see</b> is a class that reaches a store through a helper in another file. That half
    /// is measured instead: <c>scripts/test-fast.ps1</c> points both endpoints at a closed local port, so a class that
    /// should carry the trait and does not goes RED in the fast tier itself rather than quietly using the real server.
    /// </para>
    /// </remarks>
    public class FastTierTraitTests
    {
        public const string Category = "Category";
        public const string ExternalDatabase = "ExternalDatabase";

        /// <summary>Identifiers and literals that mean "this class talks to PostgreSQL, RavenDB or SQL Server".</summary>
        private static readonly string[] ExternalMarkers =
        {
            "RavenGateStore", "PostgresGateStore", "SqlServerGateStore", "PostgresTestEndpoint", "RAVENDB_TEST_URL", "ResolveStore",
            "RavenTestDatabaseJanitor", "CreateDatabaseOperation", "DocumentStore", "NpgsqlConnection",
            // The SQL Server leg of the module-aggregate conformance suites (the test server).
            "SqlServerTestEndpoint", "SqlConnection",
            // The MySQL connector's live tests (a throwaway MySQL on this machine's loopback, scripts/mysql-test-server.ps1).
            "MySqlTestEndpoint",
            // The Redis connector's live tests (a throwaway Valkey on this machine's loopback, scripts/redis-test-server.ps1).
            "RedisTestEndpoint",
            // The MongoDB connector's live tests (a throwaway MongoDB on this machine's loopback, scripts/mongodb-test-server.ps1).
            "MongoDbTestEndpoint",
            // The FTP and SFTP connectors' live tests (a throwaway Pure-FTPd and OpenSSH on this machine's loopback,
            // scripts/ftp-test-server.ps1 and scripts/sftp-test-server.ps1).
            "FileServerTestEndpoint",
            // The PostgreSQL connector against QuestDB and CrateDB (a throwaway of each on this machine's loopback,
            // scripts/questdb-test-server.ps1 and scripts/cratedb-test-server.ps1).
            "PgWireTestEndpoint",
            // The RabbitMQ connector's live tests (a throwaway RabbitMQ on this machine's loopback, scripts/rabbitmq-test-server.ps1).
            "RabbitMqTestEndpoint",
            // The MQTT connector's live tests (a throwaway Mosquitto on this machine's loopback, scripts/mqtt-test-server.ps1).
            "MqttTestEndpoint",
            // The Nextcloud connector's live tests (a throwaway Nextcloud on this machine's loopback, scripts/nextcloud-test-server.ps1).
            "NextcloudTestEndpoint",
            // The Azure Storage connector's live tests (a throwaway Azurite on this machine's loopback, scripts/azurite-test-server.ps1).
            "AzureStorageTestEndpoint",
            // The Google Cloud Storage connector's live tests (a throwaway fake-gcs-server on this machine's loopback, scripts/gcs-test-server.ps1).
            "GcsTestEndpoint",
            // The LDAP connector's live tests (a throwaway OpenLDAP on this machine's loopback, scripts/ldap-test-server.ps1).
            "LdapTestEndpoint",
            // The Home Assistant connector's live tests (a throwaway Home Assistant on this machine's loopback, scripts/homeassistant-test-server.ps1).
            "HomeAssistantTestEndpoint",
            // The Azure Cosmos DB connector's live tests (Microsoft's emulator on this machine's loopback, scripts/cosmosdb-test-server.ps1).
            "CosmosDbTestEndpoint",
            // The Google Cloud Firestore connector's live tests (Google's emulator on this machine's loopback, scripts/firestore-test-server.ps1).
            "FirestoreTestEndpoint",
            // The AWS connectors' live tests (moto on this machine's loopback, checking signatures, scripts/moto-test-server.ps1).
            "MotoTestEndpoint",
            // The Metabase connector's live tests (a real Metabase on this machine's loopback, scripts/metabase-test-server.ps1).
            "MetabaseTestEndpoint",
            // The MISP connector's live tests (a real MISP on this machine's loopback, scripts/misp-test-server.ps1).
            "MispTestEndpoint",
            // The Elastic Security connector's live tests (a real Kibana over its own Elasticsearch on this machine's loopback, scripts/kibana-test-server.ps1).
            "KibanaTestEndpoint",
            // The TheHive connector's live tests (a real TheHive 5.5 on this machine's loopback, scripts/thehive-test-server.ps1).
            "TheHiveTestEndpoint",
            // The YOURLS connector's live tests (a real YOURLS 1.10 over MySQL on this machine's loopback, scripts/yourls-test-server.ps1).
            "YourlsTestEndpoint",
            // The Wekan connector's live tests (a real Wekan 12 over MongoDB on this machine's loopback, scripts/wekan-test-server.ps1).
            "WekanTestEndpoint",
            // The Rundeck connector's live tests (a real Rundeck 6.2 on this machine's loopback, scripts/rundeck-test-server.ps1).
            "RundeckTestEndpoint",
            // The Taiga connector's live tests (a real Taiga 6.10 on this machine's loopback, scripts/taiga-test-server.ps1).
            "TaigaTestEndpoint",
            // The QuickChart connector's live tests (a real QuickChart 1.8 on this machine's loopback, scripts/quickchart-test-server.ps1).
            "QuickChartTestEndpoint",
            // The Monica CRM connector's live tests (a real Monica 4.1 on this machine's loopback, scripts/monica-test-server.ps1).
            "MonicaCrmTestEndpoint",
            // The Invoice Ninja connector's live tests (a real Invoice Ninja 5.13 on this machine's loopback, scripts/invoiceninja-test-server.ps1).
            "InvoiceNinjaTestEndpoint",
            // The Mattermost connector's live tests (a real Mattermost Team Edition 10.11 on this machine's loopback, scripts/mattermost-test-server.ps1).
            "MattermostTestEndpoint",
            // The ERPNext connector's live tests (a real ERPNext 16 on this machine's loopback, scripts/erpnext-test-server.ps1).
            "ErpNextTestEndpoint",
            // The SeaTable connector's live tests (a real SeaTable 6.2 on this machine's loopback, scripts/seatable-test-server.ps1).
            "SeaTableTestEndpoint",
        };

        /// <summary>Whole projects that are external by nature carry the trait on the ASSEMBLY.</summary>
        private static readonly string[] ExternalProjects =
        {
            "Stomme.Persistence.Conformance.Tests", "Stomme.Persistence.Raven.Tests", "Stomme.MessageBus.Live.Tests",
        };

        private readonly ITestOutputHelper _output;

        public FastTierTraitTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Every_test_class_that_reaches_an_external_database_says_so_and_no_other_class_does()
        {
            // Every test project except the ones that are external as a whole (they carry the trait on the assembly).
            var findings = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "test"), "*.cs", SearchOption.AllDirectories)
                .Where(file => !RepoRelative(file).Split('/').Any(part => part is "obj" or "bin" || ExternalProjects.Contains(part, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .SelectMany(file => Scan(File.ReadAllText(file), RepoRelative(file)))
                .ToList();

            foreach (var finding in findings)
            {
                _output.WriteLine(finding);
            }

            Assert.True(findings.Count == 0,
                $"The fast tier's trait is out of date. A test class that reaches PostgreSQL or RavenDB carries " +
                $"[Trait(\"{Category}\", \"{ExternalDatabase}\")], and a class that does not reach one does not:" +
                Environment.NewLine + string.Join(Environment.NewLine, findings));
        }

        [Fact]
        public void The_projects_that_are_external_by_nature_carry_the_trait_on_the_assembly()
        {
            foreach (var project in ExternalProjects)
            {
                var directory = Path.Combine(RepoRoot(), "test", project);
                Assert.True(Directory.Exists(directory), $"{project} is listed as external but is not there - fix the list.");

                var declared = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                    .Where(file => !RepoRelative(file).Split('/').Any(part => part is "obj" or "bin"))
                    .Any(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot()
                        .DescendantNodes().OfType<AttributeListSyntax>()
                        .Where(list => list.Target?.Identifier.ValueText == "assembly")
                        .SelectMany(list => list.Attributes)
                        .Any(attribute => attribute.Name.ToString().Contains("AssemblyTrait", StringComparison.Ordinal)
                            && attribute.ToString().Contains(ExternalDatabase, StringComparison.Ordinal)));

                Assert.True(declared, $"{project} has no [assembly: AssemblyTrait(\"{Category}\", \"{ExternalDatabase}\")], so the fast tier would run it.");
            }
        }

        [Fact]
        public void The_scanner_works_in_both_directions()
        {
            const string unmarked = "class RavenThingTests { [Fact] void M() { var s = new RavenGateStore(); } }";
            const string marked = "[Trait(\"Category\", \"ExternalDatabase\")] class RavenThingTests { [Fact] void M() { var s = new RavenGateStore(); } }";
            const string stale = "[Trait(\"Category\", \"ExternalDatabase\")] class PlainTests { [Fact] void M() { } }";
            const string prose = "class PlainTests { /// <summary>never touches RAVENDB_TEST_URL</summary>\n [Fact] void M() { } }";
            const string helper = "class RavenGateStore { void M() { var s = new DocumentStore(); } }";
            const string subclass = "class RavenLeaseTests : LeaseTests { void Init() { var s = new DocumentStore(); } }";
            const string abstractBase = "abstract class LeaseTests { [Fact] void M() { var s = PostgresTestEndpoint.Host; } }";
            const string otherTrait = "[Trait(\"Category\", \"Slow\")] class RavenThingTests { [Fact] void M() { var s = new RavenGateStore(); } }";

            const string qualified = "[Xunit.Trait(\"Category\", \"ExternalDatabase\")] class RavenThingTests { [Fact] void M() { var s = new RavenGateStore(); } }";

            Assert.Single(Scan(unmarked, "planted/Unmarked.cs"));
            Assert.Empty(Scan(qualified, "planted/Qualified.cs"));
            Assert.Empty(Scan(marked, "planted/Marked.cs"));
            Assert.Single(Scan(stale, "planted/Stale.cs"));
            Assert.Empty(Scan(prose, "planted/Prose.cs"));
            Assert.Empty(Scan(helper, "planted/Helper.cs"));
            Assert.Single(Scan(subclass, "planted/Subclass.cs"));
            Assert.Empty(Scan(abstractBase, "planted/AbstractBase.cs"));
            Assert.Single(Scan(otherTrait, "planted/OtherTrait.cs"));
        }

        private static IEnumerable<string> Scan(string source, string file)
        {
            foreach (var declaration in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                // A test class: concrete, and either named *Tests or declaring a [Fact]/[Theory] of its own. A provider
                // subclass of an abstract suite declares none, which is why the NAME counts too.
                var isAbstract = declaration.Modifiers.Any(modifier => modifier.ValueText is "abstract" or "static");
                var declaresTests = declaration.Members.OfType<MethodDeclarationSyntax>()
                    .SelectMany(method => method.AttributeLists).SelectMany(list => list.Attributes)
                    .Any(attribute => attribute.Name.ToString() is "Fact" or "Theory");
                // This class holds the markers as DATA.
                if (declaration.Identifier.ValueText == nameof(FastTierTraitTests))
                {
                    continue;
                }

                if (isAbstract || !(declaresTests || declaration.Identifier.ValueText.EndsWith("Tests", StringComparison.Ordinal)))
                {
                    continue;
                }

                // Tokens, not text: a comment that NAMES a marker is not a use of it.
                var external = declaration.DescendantTokens()
                    .Select(token => token.ValueText)
                    .Any(text => ExternalMarkers.Contains(text, StringComparer.Ordinal));

                var marked = declaration.AttributeLists.SelectMany(list => list.Attributes)
                    .Any(attribute => attribute.Name.ToString() is "Trait" or "Xunit.Trait" or "TraitAttribute" or "Xunit.TraitAttribute"
                        && attribute.ArgumentList?.Arguments.Count == 2
                        && attribute.ArgumentList.Arguments[0].ToString().Trim('"') == Category
                        && attribute.ArgumentList.Arguments[1].ToString().Trim('"') == ExternalDatabase);

                var line = declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (external && !marked)
                {
                    yield return $"[unmarked] {file}({line}): {declaration.Identifier.ValueText} reaches an external database and does not carry the trait";
                }
                else if (marked && !external)
                {
                    yield return $"[stale] {file}({line}): {declaration.Identifier.ValueText} carries the trait and reaches no external database";
                }
            }
        }

        private static string RepoRelative(string file)
        {
            var root = RepoRoot();
            var full = Path.GetFullPath(file);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                : full.Replace('\\', '/');
        }

        private static string? _repoRoot;

        private static string RepoRoot()
        {
            if (_repoRoot is not null)
            {
                return _repoRoot;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx).");
            return _repoRoot = directory!.FullName;
        }
    }
}
