#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// An entity log row ("Page Added {Page}", "Page Module Added {PageModule}", ...) carried the WHOLE entity -
    /// its permission list with role names, user ids and four timestamps per permission - serialised into
    /// <c>Log.Message</c> and again into <c>Log.Properties</c>. Measured on a stock install boot, then fixed centrally in
    /// <c>LogManager</c>: a framework entity argument is logged as its type, id and name.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class EntityLogSizeTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public EntityLogSizeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_stock_install_boot_logs_entities_as_a_summary_that_still_says_what_changed()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var rows = await ReadLogAsync(app);
                var sizes = rows.Select(row => (row.Message?.Length ?? 0) + (row.Properties?.Length ?? 0)).OrderByDescending(size => size).ToList();
                _output.WriteLine($"[log] {rows.Count} rows; message+properties total {sizes.Sum()} chars, max {sizes.FirstOrDefault()}, " +
                    $"median {sizes.ElementAtOrDefault(sizes.Count / 2)}");
                foreach (var row in rows.OrderByDescending(row => (row.Message?.Length ?? 0) + (row.Properties?.Length ?? 0)).Take(5))
                {
                    _output.WriteLine($"[log] {(row.Message?.Length ?? 0) + (row.Properties?.Length ?? 0),7}  {Clip(row.MessageTemplate)}");
                }

                // Positive control: the page-creation rows still NAME the page they created...
                var pageAdded = rows.Where(row => row.MessageTemplate?.StartsWith("Page Added", StringComparison.Ordinal) == true).ToList();
                Assert.NotEmpty(pageAdded);
                Assert.All(pageAdded, row => Assert.Matches(@"Page \d+", row.Message));

                // ...without carrying its permission graph, twice.
                Assert.All(rows, row => Assert.DoesNotContain("PermissionList", (row.Message ?? "") + (row.Properties ?? ""), StringComparison.OrdinalIgnoreCase));
                Assert.True(sizes.First() < 2_000, $"the largest log row is {sizes.First()} characters");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static async Task<List<Log>> ReadLogAsync(WebApplication app)
        {
            // The drain writes on a background thread; wait until the boot's rows have landed and stopped growing.
            List<Log> rows = new();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var read = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<ILogRepository>().GetLogsAsync(alias.SiteId, null!, null!, 5_000)).ToList());
                if (read.Count > 0 && read.Count == rows.Count)
                {
                    return read;
                }

                rows = read;
                await Task.Delay(500);
            }

            return rows;
        }

        private static string Clip(string? value) => value is null ? "" : value.Length <= 80 ? value : value[..80];
    }
}
