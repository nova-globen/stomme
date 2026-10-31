#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Models.Erasure;
using Stomme.Modules.Erasure.Application;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Erasure
{
    /// <summary>
    /// What deleting an account says it does (BL-276): the list the profile page shows before the person confirms, built
    /// from the erasure participants' declarations - grouped, ordered, readable, and served to anyone.
    /// </summary>
    public sealed class ErasureSummaryTests
    {
        [Fact]
        public void The_list_is_deleted_then_anonymised_then_retained_with_each_line_once_and_a_name_where_no_summary_is_given()
        {
            var items = ErasureSummary.Build(new ISubjectErasureParticipant[]
            {
                new Participant("Audit", ErasureDeclaration.Retain("AuditAggregate", RetentionReason.Security, TimeSpan.FromDays(365)) with { Summary = "the security record" }),
                new Participant("Ledger", ErasureDeclaration.Anonymise("LedgerAggregate") with { Summary = "your usage (the counts stay)" }),
                new Participant("Notes", ErasureDeclaration.Delete("NoteAggregate") with { Summary = "your notes" }, ErasureDeclaration.Delete("NoteDraftAggregate")),
                new Participant("Notes again", ErasureDeclaration.Delete("NoteCopyAggregate") with { Summary = "your notes" }),
            });

            Assert.Equal(new[] { "Notes", "your notes", "your usage (the counts stay)", "the security record" }, items.Select(item => item.Description));
            Assert.Equal(new[] { "Delete", "Delete", "Anonymise", "Retain" }, items.Select(item => item.Handling));
            Assert.Equal("Security", items[3].Reason);
            Assert.Equal(365, items[3].RetentionDays);
            Assert.Null(items[0].RetentionDays);
        }

        /// <summary>
        /// Every declaration the framework ships names what it erases in the person's words: a missing summary would
        /// put a participant's internal name in front of the person deciding to delete their account. External modules
        /// fall back to their participant's name; in-tree ones may not.
        /// </summary>
        [Fact]
        public void Every_declaration_in_the_framework_carries_a_summary()
        {
            var declaration = new Regex(@"ErasureDeclaration\.(Delete|Anonymise|Retain)\(""[^""]*""[^)]*\)(?<rest>[^\r\n]{0,20})");
            var source = Path.Combine(RepoRoot(), "src", "Stomme.Server");
            var found = 0;
            var missing = new List<string>();
            foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in declaration.Matches(File.ReadAllText(file)))
                {
                    found++;
                    if (!match.Groups["rest"].Value.TrimStart().StartsWith("with { Summary", StringComparison.Ordinal))
                    {
                        missing.Add($"{Path.GetRelativePath(source, file)}: {match.Value}");
                    }
                }
            }

            Assert.True(found >= 25, $"only {found} declarations found - the scan no longer matches how they are written");
            Assert.True(missing.Count == 0, "Declarations without a Summary:\n" + string.Join("\n", missing));
        }

        private sealed class Participant(string name, params ErasureDeclaration[] declarations) : ISubjectErasureParticipant
        {
            public string Name => name;

            public IReadOnlyList<ErasureDeclaration> Declarations => declarations;

            public Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<ErasureOutcome>>(Array.Empty<ErasureOutcome>());
        }

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx).");
            return directory!.FullName;
        }
    }

    /// <summary>The summary on an installed site, asked for anonymously - what a privacy page or the profile page reads.</summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ErasureSummaryDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task An_installed_site_answers_anyone_with_what_deleting_an_account_does()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var response = await GateDrive.GetAsync(client, null, Host, "/api/Erasure/summary", output, "the erasure summary, anonymous");
                Assert.Equal(HttpStatusCode.OK, response.Status);

                var items = JsonSerializer.Deserialize<List<ErasureSummaryItem>>(response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Contains(items, item => item.Handling == "Delete" && item.Description == "your conversations with the AI assistant");
                Assert.Contains(items, item => item.Handling == "Anonymise" && item.Description == "your AI usage records (the counts stay)");
                Assert.Contains(items, item => item.Handling == "Retain" && item.Reason == "Security" && item.RetentionDays == 365
                    && item.Description == "the security record of your sign-ins and account changes");
                Assert.DoesNotContain(items, item => item.Description.EndsWith("Aggregate", StringComparison.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
