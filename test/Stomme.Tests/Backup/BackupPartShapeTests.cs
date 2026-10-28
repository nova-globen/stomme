#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Backup.Participants;
using Stomme.Persistence.File;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-170 step (1): a backup part's schema version is the SHAPE of what it carries, and a restore takes an archive from
    /// the same version or an older one whose parts' shapes are the host's own.
    /// </summary>
    /// <remarks>
    /// Every participant declared <c>SchemaVersion => "1"</c> and nothing bumped it, so the only guard against an archive's
    /// rows meeting a newer shape was the manifest's exact framework version - any upgrade made every earlier archive
    /// unrestorable, even when nothing it carries had changed. The shape is read from the serializer that writes the rows
    /// (its contract: every written property's name and type, recursively), and the framework part adds the master and tenant
    /// migrations, so a column added between versions refuses the older part by name while an unchanged one restores.
    /// </remarks>
    public sealed class BackupPartShapeTests
    {
        private sealed class Code : IStringValueObject
        {
            public string Value { get; private set; } = "";
        }

        private sealed class Line
        {
            public int Quantity { get; private set; }
        }

        private sealed class Sample : AggregateRoot<Guid>
        {
            public string Name { get; private set; } = "";
            public Code? Code { get; private set; }
            public List<Line> Lines { get; private set; } = new();
            public string Describe() => Name;
        }

        private sealed class Other : AggregateRoot<Guid>
        {
            public DateTime At { get; private set; }
        }

        [Fact]
        public void The_shape_is_what_the_serializer_writes_recursively_and_nothing_else()
        {
            var text = FileStoreSerializer.ShapeText(new[] { typeof(Sample) });

            Assert.Contains("Name:System.String;", text);
            Assert.Contains("Lines:System.Collections.Generic.List`1[", text);
            Assert.Contains("Quantity:System.Int32;", text);                 // a held object is described too
            Assert.Matches(new Regex(@"Code:Stomme\.Tests\.Backup\.BackupPartShapeTests\+Code;"), text); // a scalar value object: its type alone
            Assert.DoesNotContain("DomainEvents", text);                     // the serializer ignores it
            Assert.DoesNotContain("Describe", text);                         // a method is no part of the data
        }

        [Fact]
        public void The_shape_ignores_order_and_changes_with_anything_added()
        {
            var both = FileStoreSerializer.Shape(new[] { typeof(Sample), typeof(Other) });
            Assert.Equal(both, FileStoreSerializer.Shape(new[] { typeof(Other), typeof(Sample) }));
            Assert.StartsWith("shape-", both);
            Assert.NotEqual(both, FileStoreSerializer.Shape(new[] { typeof(Sample) }));
            Assert.NotEqual(both, FileStoreSerializer.Shape(new[] { typeof(Sample), typeof(Other) }, new[] { "TenantDBContext:Tenant.99.00.00.01" }));
        }

        [Fact]
        public void Every_participant_carries_its_shape_not_a_hand_kept_number()
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            var framework = new FrameworkDatabasesBackupParticipant(scopes);
            var openIddict = new OpenIddictBackupParticipant(scopes);

            Assert.StartsWith("shape-", framework.SchemaVersion);
            Assert.Equal(framework.SchemaVersion, new FrameworkDatabasesBackupParticipant(scopes).SchemaVersion);
            Assert.StartsWith("shape-", openIddict.SchemaVersion);
            Assert.NotEqual(framework.SchemaVersion, openIddict.SchemaVersion);

            // A module aggregate's part is that aggregate's shape: a module that adds a property refuses its older parts.
            var module = new ModuleAggregateBackupParticipant<Sample, Guid, Microsoft.EntityFrameworkCore.DbContext>(scopes);
            Assert.Equal(FileStoreSerializer.Shape(new[] { typeof(Sample) }), module.SchemaVersion);
            Assert.NotEqual(module.SchemaVersion, new ModuleAggregateBackupParticipant<Other, Guid, Microsoft.EntityFrameworkCore.DbContext>(scopes).SchemaVersion);

            // The framework part's migrations are the host's own, master and tenant, read from the assembly.
            var migrations = FrameworkDatabasesBackupParticipant.FrameworkMigrations().ToList();
            Assert.Contains("TenantDBContext:Tenant.10.02.00.06", migrations);
            Assert.Contains(migrations, migration => migration.StartsWith("MasterDBContext:", StringComparison.Ordinal));
            Assert.True(migrations.Count > 40, $"only {migrations.Count} migrations found - the scan is broken");
        }

        [Fact]
        public void Upgrade_step_versions_list_exactly_the_versions_the_upgrade_switch_has_a_case_for()
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "Infrastructure", "UpgradeManager.cs"));
            var code = string.Join("\n", source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            var cases = Regex.Matches(code, "case \"(?<version>[0-9.]+)\":").Select(match => match.Groups["version"].Value).OrderBy(v => v).ToList();

            Assert.Equal(cases, UpgradeManager.StepVersions.OrderBy(v => v).ToList());

            // The scanner sees a case when there is one (the known-dirty control, on a copy of the source).
            Assert.Single(Regex.Matches("switch (version) { case \"10.9.0\": Upgrade_10_9_0(); break; }", "case \"(?<version>[0-9.]+)\":"));
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
        }
    }
}
