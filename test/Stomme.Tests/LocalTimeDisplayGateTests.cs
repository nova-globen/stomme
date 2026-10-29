using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-22's depth-2 pass - a list shows its times in the viewer's time zone. Every audit time (CreatedOn,
    /// ModifiedOn, DeletedOn, ...) is stored in UTC; the Recycle Bin printed DeletedOn as it was stored, so a page deleted at
    /// 11:44 local read "9:44 AM" beside lists that convert - and the profile's notification lists and the HtmlText version
    /// history did the same. This gate reads every page and control: a row's <c>context.SomethingOn</c> is shown through
    /// <c>UtcToLocal</c>, or refused by name and file.
    /// </summary>
    public sealed class LocalTimeDisplayGateTests
    {
        // A row's audit time, the property named ...On, not passed through UtcToLocal(...) on the same reference.
        internal static readonly Regex RawUtcTime = new(@"(?<!UtcToLocal\()\bcontext\.\w+On\b", RegexOptions.Compiled);

        [Fact]
        public void No_list_shows_a_stored_UTC_time_as_if_it_were_local()
        {
            var root = RepositoryRoot();
            var offenders = new[] { Path.Combine("src", "Stomme.Client"), "examples" }
                .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.razor", SearchOption.AllDirectories))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .SelectMany(file => File.ReadAllLines(file)
                    .Select((line, index) => (File: Path.GetRelativePath(root, file), Line: index + 1, Text: line.Trim()))
                    .Where(line => RawUtcTime.IsMatch(line.Text)))
                .ToList();

            Assert.True(offenders.Count == 0, "A stored UTC time shown as if it were local - show it through UtcToLocal(...):" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Select(found => $"{found.File}:{found.Line}: {found.Text}")));
        }

        [Theory]
        [InlineData("<td>@context.DeletedOn</td>", true)]
        [InlineData("<td>@string.Format(\"{0:dd-MMM-yyyy HH:mm:ss}\", @context.CreatedOn)</td>", true)]
        [InlineData("Message=\"@string.Format(Localizer[\"Confirm.Restore\"], context.CreatedOn)\"", true)]
        [InlineData("<td>@UtcToLocal(context.DeletedOn)</td>", false)]
        [InlineData("<td>@string.Format(\"{0:dd-MMM-yyyy HH:mm:ss}\", UtcToLocal(context.CreatedOn))</td>", false)]
        [InlineData("<td>@context.EnqueuedUtc.ToString(\"u\")</td>", false)]
        [InlineData("<td>@context.Name</td>", false)]
        public void The_scanner_finds_a_raw_audit_time_and_nothing_else(string markup, bool flagged) =>
            Assert.Equal(flagged, RawUtcTime.IsMatch(markup));

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
