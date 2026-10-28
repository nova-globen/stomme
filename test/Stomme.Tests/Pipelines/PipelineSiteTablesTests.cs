#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Data tables: tables a site keeps for its pipelines (<see cref="PipelineSiteTables"/>), kept one key per
    /// table and one per row in the engine's key-value store under the site (<see cref="PipelineSiteTableStore"/>), and the Data
    /// table step that gets, inserts, updates, upserts and deletes their rows - each value converted to what its column holds,
    /// the Filter step's rules choosing rows, and nothing that would change every row by mistake.
    /// </summary>
    public sealed class PipelineSiteTablesTests : IAsyncLifetime
    {
        private static readonly DateTimeOffset Moment = new(2026, 10, 7, 6, 0, 0, TimeSpan.Zero);

        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new Silent());
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<PipelineSiteVariablesCache>();
            services.AddSingleton<ISystemClock>(new MovingClock(Moment));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class Silent : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => TextWriter.Null;
        }

        // A millisecond passes each time the clock is read - as it does between one step and the next.
        private sealed class MovingClock(DateTimeOffset start) : ISystemClock
        {
            private long _reads;

            public DateTimeOffset UtcNow => start.AddMilliseconds(Interlocked.Increment(ref _reads));
        }

        private IKeyValueStore KeyValues => _services.GetRequiredService<IKeyValueStore>();

        private string? Tenant => _services.GetRequiredService<ITenantAccessor>().TenantId;

        private async Task<StoredSiteTable> SubscribersAsync() =>
            await PipelineSiteTableStore.CreateAsync(KeyValues, Tenant, new PipelineSiteTableRequest
            {
                Name = "Subscribers",
                Columns =
                {
                    new PipelineTableColumn { Name = "email", Type = PipelineTableColumnType.Text },
                    new PipelineTableColumn { Name = "visits", Type = PipelineTableColumnType.Number },
                    new PipelineTableColumn { Name = "active", Type = PipelineTableColumnType.Boolean },
                },
            }, "t-1", Moment, CancellationToken.None);

        private async Task<List<JsonObject>> RowsAsync(string tableId = "t-1") =>
            await PipelineSiteTableStore.ReadRowsAsync(KeyValues, Tenant, tableId, CancellationToken.None);

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private async Task<(int Count, JsonArray Rows)> ResultOf(DataTable step) =>
            await ResultOf(step, _ => { });

        private async Task<(int Count, JsonArray Rows)> ResultOf(DataTable step, Action<IDictionary<string, object>> outputs)
        {
            var run = await RunAsync(step);
            Assert.True(run.PipelineState.SubStatus == PipelineSubStatus.Finished,
                $"{run.PipelineState.SubStatus}: {string.Join(" | ", run.PipelineState.Incidents.Select(i => i.Message))}");
            var all = run.Journal.StepExecutionContexts.Single(c => c.Step is DataTable).GetOutputs();
            outputs(all);
            return ((int)all["Count"], JsonNode.Parse((string)all["Result"])!.AsArray());
        }

        private async Task<string> FaultOf(DataTable step)
        {
            var run = await RunAsync(step);
            Assert.Equal(PipelineSubStatus.Faulted, run.PipelineState.SubStatus);
            return string.Join(" | ", run.PipelineState.Incidents.Select(i => i.Message));
        }

        private static string Rules(params (string Left, string Operator, string Right)[] rules) =>
            PipelineConditions.Write(new PipelineCondition
            {
                Match = PipelineConditions.All,
                Rules = rules.Select(rule => new PipelineConditionRule { Left = rule.Left, Operator = rule.Operator, Right = rule.Right }).ToList(),
            });

        private static DataTable Step(DataTableOperation operation, string? records = null, string? condition = null, string? values = null, string? match = null, int max = 0, string table = "Subscribers") => new()
        {
            Table = new(table),
            Operation = new(operation),
            Records = records is null ? null : new(records),
            Condition = condition is null ? null : new(condition),
            Values = values is null ? null : new(values),
            MatchColumns = match is null ? null : new(match),
            MaxRows = new(max),
        };

        private static string Text(JsonObject row, string column) => PipelineSiteTables.Text(row[column]);

        [Fact]
        public void A_table_names_its_columns_and_a_value_is_kept_as_its_column_holds_it()
        {
            var columns = new List<PipelineTableColumn> { new() { Name = "email" }, new() { Name = "visits", Type = PipelineTableColumnType.Number } };
            Assert.True(PipelineSiteTables.Check("Subscribers", columns, out var error), error);
            Assert.False(PipelineSiteTables.Check(" ", columns, out _));
            Assert.False(PipelineSiteTables.Check("Subscribers", new List<PipelineTableColumn>(), out error));
            Assert.Equal("A table has at least one column.", error);
            Assert.False(PipelineSiteTables.Check("Subscribers", new List<PipelineTableColumn> { new() { Name = "id" } }, out error));
            Assert.StartsWith("'id' cannot be a column's name", error);
            Assert.False(PipelineSiteTables.Check("Subscribers", new List<PipelineTableColumn> { new() { Name = "2fa" } }, out _));
            Assert.False(PipelineSiteTables.Check("Subscribers", new List<PipelineTableColumn> { new() { Name = "Email" }, new() { Name = "email" } }, out error));
            Assert.StartsWith("Two columns are named", error);

            Assert.True(PipelineSiteTables.TryConvert("12.50", PipelineTableColumnType.Number, out var number, out _));
            Assert.Equal("12.5", PipelineSiteTables.Text(number));
            Assert.False(PipelineSiteTables.TryConvert("many", PipelineTableColumnType.Number, out _, out error));
            Assert.Equal("'many' is not a number", error);
            Assert.True(PipelineSiteTables.TryConvert("Yes", PipelineTableColumnType.Boolean, out var yes, out _));
            Assert.Equal("true", PipelineSiteTables.Text(yes));
            Assert.True(PipelineSiteTables.TryConvert("0", PipelineTableColumnType.Boolean, out var no, out _));
            Assert.Equal("false", PipelineSiteTables.Text(no));
            Assert.False(PipelineSiteTables.TryConvert("maybe", PipelineTableColumnType.Boolean, out _, out _));
            Assert.True(PipelineSiteTables.TryConvert("2026-10-07T08:00:00+02:00", PipelineTableColumnType.Date, out var date, out _));
            Assert.Equal("2026-10-07T06:00:00Z", PipelineSiteTables.Text(date));
            Assert.False(PipelineSiteTables.TryConvert("May", PipelineTableColumnType.Date, out _, out _));
            Assert.True(PipelineSiteTables.TryConvert(new JsonArray("a", "b"), PipelineTableColumnType.Text, out var list, out _));
            Assert.Equal("[\"a\",\"b\"]", PipelineSiteTables.Text(list));
            Assert.False(PipelineSiteTables.TryConvert(new JsonObject { ["a"] = 1 }, PipelineTableColumnType.Number, out _, out error));
            Assert.Equal("is a record, not a number", error);
            Assert.True(PipelineSiteTables.TryConvert(JsonValue.Create(""), PipelineTableColumnType.Number, out var empty, out _));
            Assert.Null(empty);

            Assert.Equal(new[] { "visits", "email" }, PipelineSiteTables.ParseValues("visits: 3\nemail: ada@example.com", out _)!.Keys);
            Assert.Equal("3", PipelineSiteTables.ParseValues("{\"visits\": 3}", out _)!["visits"]!.ToJsonString());
            Assert.Null(PipelineSiteTables.ParseValues("visits", out error));
            Assert.Equal("'visits' is not 'column: value'.", error);
        }

        [Fact]
        public async Task A_table_is_made_changed_and_deleted_with_its_rows_and_seen_only_on_its_own_site()
        {
            var table = await SubscribersAsync();
            var refused = await Assert.ThrowsAsync<ArgumentException>(() => PipelineSiteTableStore.CreateAsync(KeyValues, Tenant, new PipelineSiteTableRequest { Name = "subscribers", Columns = { new PipelineTableColumn { Name = "x" } } }, "t-2", Moment, CancellationToken.None));
            Assert.Equal("There is already a data table named 'subscribers'.", refused.Message);

            // Rows by hand, as the page adds and changes them: each value as its column holds it, or refused saying why.
            var add = new AddTableRowCommandHandler(KeyValues, _services.GetRequiredService<ITenantAccessor>(), _services.GetRequiredService<ISystemClock>());
            var added = await add.Handle(new AddTableRowCommand("t-1", new PipelineTableRowRequest { Values = { ["email"] = "ada@example.com", ["visits"] = "3", ["active"] = "yes" } }), CancellationToken.None);
            Assert.Equal("3", added!["visits"]);
            Assert.Equal("true", added["active"]);
            Assert.Matches(@"^2026-10-07T06:00:00\.\d{3}Z$", added[PipelineSiteTables.CreatedColumn]);
            var update = new UpdateTableRowCommandHandler(KeyValues, _services.GetRequiredService<ITenantAccessor>(), _services.GetRequiredService<ISystemClock>());
            var wrong = await Assert.ThrowsAsync<ArgumentException>(() => update.Handle(new UpdateTableRowCommand("t-1", added["id"], new PipelineTableRowRequest { Values = { ["visits"] = "many" } }), CancellationToken.None));
            Assert.Equal("visits: 'many' is not a number.", wrong.Message);
            Assert.Equal("4", (await update.Handle(new UpdateTableRowCommand("t-1", added["id"], new PipelineTableRowRequest { Values = { ["visits"] = "4" } }), CancellationToken.None))!["visits"]);
            await Assert.ThrowsAsync<ArgumentException>(() => update.Handle(new UpdateTableRowCommand("t-1", added["id"], new PipelineTableRowRequest { Values = { ["nope"] = "1" } }), CancellationToken.None));

            // Renamed, a column added and one removed; a column's type does not change.
            var typeChanged = await Assert.ThrowsAsync<ArgumentException>(() => PipelineSiteTableStore.UpdateAsync(KeyValues, Tenant, "t-1", new PipelineSiteTableRequest { Name = "Subscribers", Columns = { new PipelineTableColumn { Name = "visits", Type = PipelineTableColumnType.Text } } }, Moment, CancellationToken.None));
            Assert.StartsWith("'visits' holds a number; a column's type does not change", typeChanged.Message);
            await PipelineSiteTableStore.UpdateAsync(KeyValues, Tenant, "t-1", new PipelineSiteTableRequest
            {
                Name = "Members",
                Columns = { new PipelineTableColumn { Name = "email" }, new PipelineTableColumn { Name = "joined", Type = PipelineTableColumnType.Date } },
            }, Moment, CancellationToken.None);
            var renamed = (await PipelineSiteTableStore.FindAsync(KeyValues, Tenant, "members", CancellationToken.None))!;
            Assert.Equal(new[] { "email", "joined" }, renamed.Columns.Select(column => column.Name));
            var row = PipelineSiteTableStore.Project(renamed, Assert.Single(await RowsAsync()));
            Assert.Equal(new[] { "id", "createdAt", "updatedAt", "email", "joined" }, row.Select(property => property.Key));
            Assert.Null(row["joined"]);

            // Another site sees none of it.
            Assert.Empty(await PipelineSiteTableStore.ListAsync(KeyValues, "another-site", CancellationToken.None));
            Assert.Null(await PipelineSiteTableStore.FindAsync(KeyValues, "another-site", "Members", CancellationToken.None));
            Assert.Empty(await PipelineSiteTableStore.ReadRowsAsync(KeyValues, "another-site", "t-1", CancellationToken.None));

            // Deleted: the table and every row of it.
            Assert.True(await PipelineSiteTableStore.DeleteAsync(KeyValues, Tenant, "t-1", CancellationToken.None));
            Assert.Empty(await RowsAsync());
            Assert.Empty(await PipelineSiteTableStore.ListAsync(KeyValues, Tenant, CancellationToken.None));
            Assert.False(await PipelineSiteTableStore.DeleteAsync(KeyValues, Tenant, "t-1", CancellationToken.None));
        }

        // BL-229: n8n's import into a data table, and its download - a CSV's header names the columns (id, createdAt and updatedAt
        // the table's own, any other name passed over and named back), the delimiter the header's; all or nothing.
        [Fact]
        public async Task A_csv_adds_rows_all_or_nothing_and_a_table_downloads_as_one()
        {
            await SubscribersAsync();
            var tenants = _services.GetRequiredService<ITenantAccessor>();
            var import = new ImportTableCsvCommandHandler(KeyValues, tenants, _services.GetRequiredService<ISystemClock>());

            var refused = await Assert.ThrowsAsync<ArgumentException>(() => import.Handle(new ImportTableCsvCommand("t-1", "email;visits\nada@example.com;3\nbob@example.com;many\n"), CancellationToken.None));
            Assert.Equal("Row 3, visits: 'many' is not a number. Nothing was imported.", refused.Message);
            Assert.Empty(await RowsAsync());
            var none = await Assert.ThrowsAsync<ArgumentException>(() => import.Handle(new ImportTableCsvCommand("t-1", "name,city\nAda,London\n"), CancellationToken.None));
            Assert.Equal("None of the CSV's columns (name, city) is a column of 'Subscribers' (email, visits, active).", none.Message);

            var result = await import.Handle(new ImportTableCsvCommand("t-1", "\uFEFFid;Email;visits;source\nx1;ada@example.com;3;web\n;\"bob@example.com\";\"1\";mail\n"), CancellationToken.None);
            Assert.Equal(2, result!.Imported);
            Assert.Equal(new[] { "source" }, result.Ignored);
            Assert.Equal(new[] { "ada@example.com:3", "bob@example.com:1" }, (await RowsAsync()).Select(row => Text(row, "email") + ":" + Text(row, "visits")));
            Assert.NotEqual("x1", PipelineSiteTableStore.IdOf((await RowsAsync())[0])); // the id is the table's own
            Assert.Null(await import.Handle(new ImportTableCsvCommand("no-such-table", "email\n"), CancellationToken.None));

            var csv = (await new ExportTableCsvQueryHandler(KeyValues, tenants).Handle(new ExportTableCsvQuery("t-1"), CancellationToken.None))!;
            Assert.Equal("Subscribers.csv", csv.FileName);
            var lines = csv.Csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("id,createdAt,updatedAt,email,visits,active", lines[0]);
            Assert.EndsWith(",ada@example.com,3,", lines[1]);
            Assert.Equal(3, lines.Length);
        }

        [Fact]
        public async Task The_step_inserts_gets_updates_upserts_and_deletes_rows()
        {
            await SubscribersAsync();

            // Insert: one row per record, its values as the columns hold them; a field that is no column passed over.
            var (count, rows) = await ResultOf(Step(DataTableOperation.Insert, records: """[{"email":"ada@example.com","visits":"3","active":"yes","source":"web"},{"email":"bob@example.com","visits":1}]"""));
            Assert.Equal(2, count);
            Assert.Equal(new[] { "ada@example.com", "bob@example.com" }, rows.Select(row => (string)row!["email"]!));
            Assert.Null(rows[0]!["source"]);
            var kept = await RowsAsync();
            Assert.Equal(new[] { "3", "1" }, kept.Select(row => Text(row, "visits")));
            Assert.Equal("true", Text(kept[0], "active"));
            Assert.False(kept[0].ContainsKey("source"));

            // Get: the rows meeting the rules ({{ column }} reads the row's), or every row, at most so many.
            (count, rows) = await ResultOf(Step(DataTableOperation.Get, condition: Rules(("{{ visits }}", "at-least", "2"))));
            Assert.Equal("ada@example.com", (string)Assert.Single(rows)!["email"]!);
            Assert.Equal(1, count);
            (count, rows) = await ResultOf(Step(DataTableOperation.Get, max: 1));
            Assert.Equal("ada@example.com", (string)Assert.Single(rows)!["email"]!);

            // Update: the values set on the rows meeting the rules.
            (count, _) = await ResultOf(Step(DataTableOperation.Update, condition: Rules(("{{ email }}", "equals", "bob@example.com")), values: "visits: 5\nactive: no"));
            Assert.Equal(1, count);
            kept = await RowsAsync();
            Assert.Equal(new[] { "3", "5" }, kept.Select(row => Text(row, "visits")));
            Assert.Equal("false", Text(kept[1], "active"));

            // Upsert: a row agreeing on the match columns updated, a record no row agrees with a new row.
            // Inserted: only the rows it made new - what a pipeline has not seen before.
            IDictionary<string, object> upserted = null!;
            (count, rows) = await ResultOf(Step(DataTableOperation.Upsert, records: """[{"email":"bob@example.com","visits":6},{"email":"cy@example.com","visits":1}]""", match: "email"), outputs => upserted = outputs);
            Assert.Equal(2, count);
            Assert.Equal("cy@example.com", (string)Assert.Single(JsonNode.Parse((string)upserted["Inserted"])!.AsArray())!["email"]!);
            Assert.Equal(1, upserted["InsertedCount"]);
            kept = await RowsAsync();
            Assert.Equal(new[] { "ada@example.com:3", "bob@example.com:6", "cy@example.com:1" }, kept.Select(row => Text(row, "email") + ":" + Text(row, "visits")));
            Assert.Equal(PipelineSiteTableStore.IdOf(kept[1]), (string)rows.Single(row => (string)row!["email"]! == "bob@example.com")!["id"]!);

            // Delete: the rows meeting the rules.
            (count, rows) = await ResultOf(Step(DataTableOperation.Delete, condition: Rules(("{{ visits }}", "less", "2"))));
            Assert.Equal("cy@example.com", (string)Assert.Single(rows)!["email"]!);
            Assert.Equal(new[] { "ada@example.com", "bob@example.com" }, (await RowsAsync()).Select(row => Text(row, "email")));

            // The table found by name whatever its case, and a pipeline's records reach it as the previous step's result too.
            (count, _) = await ResultOf(Step(DataTableOperation.Get, table: "SUBSCRIBERS"));
            Assert.Equal(2, count);
        }

        // BL-229: n8n's data table view - a column renamed keeps its values (and a removed one's go), and the rows are found
        // by any value and sorted by a column as it holds its values: numbers as numbers, dates as dates, text whatever its case.
        [Fact]
        public async Task A_renamed_column_keeps_its_values_and_rows_are_found_and_sorted_as_their_columns_hold_them()
        {
            await SubscribersAsync();
            var add = new AddTableRowCommandHandler(KeyValues, _services.GetRequiredService<ITenantAccessor>(), _services.GetRequiredService<ISystemClock>());
            foreach (var (email, visits, active) in new[] { ("ada@example.com", "9", "yes"), ("Bob@example.com", "10", ""), ("cy@example.com", "", "no") })
            {
                await add.Handle(new AddTableRowCommand("t-1", new PipelineTableRowRequest { Values = { ["email"] = email, ["visits"] = visits, ["active"] = active } }), CancellationToken.None);
            }

            var rows = new GetTableRowsQueryHandler(KeyValues, _services.GetRequiredService<ITenantAccessor>());
            async Task<string[]> Emails(string? search = null, string? sort = null, bool descending = false) =>
                (await rows.Handle(new GetTableRowsQuery("t-1", 0, 100, search, sort, descending), CancellationToken.None))!.Rows.Select(row => row["email"]).ToArray();
            Assert.Equal(new[] { "ada@example.com", "Bob@example.com", "cy@example.com" }, await Emails());
            Assert.Equal(new[] { "ada@example.com", "Bob@example.com", "cy@example.com" }, await Emails(sort: "visits")); // 9 before 10; empty last
            Assert.Equal(new[] { "Bob@example.com", "ada@example.com", "cy@example.com" }, await Emails(sort: "visits", descending: true));
            Assert.Equal(new[] { "cy@example.com", "Bob@example.com", "ada@example.com" }, await Emails(sort: "email", descending: true)); // whatever the case
            Assert.Equal(new[] { "Bob@example.com" }, await Emails(search: "BOB"));
            Assert.Equal(3, (await rows.Handle(new GetTableRowsQuery("t-1", 0, 1, Search: "example.com", Sort: "visits"), CancellationToken.None))!.Total);

            // Renamed: email to address, visits and active kept as they are - each value stays with its column.
            await PipelineSiteTableStore.UpdateAsync(KeyValues, Tenant, "t-1", new PipelineSiteTableRequest
            {
                Name = "Subscribers",
                Columns =
                {
                    new PipelineTableColumn { Name = "address", Type = PipelineTableColumnType.Text, Was = "email" },
                    new PipelineTableColumn { Name = "visits", Type = PipelineTableColumnType.Number, Was = "visits" },
                    new PipelineTableColumn { Name = "active", Type = PipelineTableColumnType.Boolean, Was = "active" },
                },
            }, Moment, CancellationToken.None);
            var page = (await rows.Handle(new GetTableRowsQuery("t-1", 0, 100), CancellationToken.None))!;
            Assert.Equal(new[] { "ada@example.com:9:true", "Bob@example.com:10:", "cy@example.com::false" }, page.Rows.Select(row => row["address"] + ":" + row["visits"] + ":" + row["active"]));
            Assert.False(page.Rows[0].ContainsKey("email"));

            var renamedAgain = new PipelineSiteTableRequest { Name = "Subscribers", Columns = { new PipelineTableColumn { Name = "address", Was = "address" }, new PipelineTableColumn { Name = "count", Type = PipelineTableColumnType.Number, Was = "visits" } } };
            await PipelineSiteTableStore.UpdateAsync(KeyValues, Tenant, "t-1", renamedAgain, Moment, CancellationToken.None); // visits to count, active removed
            Assert.Equal(new[] { "9", "10", "" }, (await rows.Handle(new GetTableRowsQuery("t-1", 0, 100), CancellationToken.None))!.Rows.Select(row => row["count"]));

            var unknown = await Assert.ThrowsAsync<ArgumentException>(() => PipelineSiteTableStore.UpdateAsync(KeyValues, Tenant, "t-1", new PipelineSiteTableRequest { Name = "Subscribers", Columns = { new PipelineTableColumn { Name = "x", Was = "nope" } } }, Moment, CancellationToken.None));
            Assert.Equal("'Subscribers' has no column 'nope' to rename.", unknown.Message);
            var twice = await Assert.ThrowsAsync<ArgumentException>(() => PipelineSiteTableStore.UpdateAsync(KeyValues, Tenant, "t-1", new PipelineSiteTableRequest { Name = "Subscribers", Columns = { new PipelineTableColumn { Name = "a", Was = "address" }, new PipelineTableColumn { Name = "b", Was = "address" } } }, Moment, CancellationToken.None));
            Assert.Equal("The column 'address' is renamed twice.", twice.Message);
            var retyped = await Assert.ThrowsAsync<ArgumentException>(() => PipelineSiteTableStore.UpdateAsync(KeyValues, Tenant, "t-1", new PipelineSiteTableRequest { Name = "Subscribers", Columns = { new PipelineTableColumn { Name = "visits", Type = PipelineTableColumnType.Text, Was = "count" } } }, Moment, CancellationToken.None));
            Assert.StartsWith("'count' holds a number; a column's type does not change", retyped.Message);

            // Two columns that swap names swap their values: every row is rewritten at once from what it held.
            await PipelineSiteTableStore.CreateAsync(KeyValues, Tenant, new PipelineSiteTableRequest { Name = "Pairs", Columns = { new PipelineTableColumn { Name = "a" }, new PipelineTableColumn { Name = "b" } } }, "t-2", Moment, CancellationToken.None);
            await add.Handle(new AddTableRowCommand("t-2", new PipelineTableRowRequest { Values = { ["a"] = "first", ["b"] = "second" } }), CancellationToken.None);
            await PipelineSiteTableStore.UpdateAsync(KeyValues, Tenant, "t-2", new PipelineSiteTableRequest { Name = "Pairs", Columns = { new PipelineTableColumn { Name = "a", Was = "b" }, new PipelineTableColumn { Name = "b", Was = "a" } } }, Moment, CancellationToken.None);
            var pair = Assert.Single((await rows.Handle(new GetTableRowsQuery("t-2", 0, 10), CancellationToken.None))!.Rows);
            Assert.Equal(("second", "first"), (pair["a"], pair["b"]));
        }

        // BL-229: a table made by the step when asked to - a column per field, typed by what all its values are - so a starter that
        // keeps a feed's items runs on its first schedule; without the switch, a missing table fails the step as before.
        [Fact]
        public async Task An_insert_or_upsert_makes_its_table_when_asked_and_the_next_run_finds_nothing_new()
        {
            const string Items = """[{"title":"One","link":"https://example.com/1","published":"2026-10-07T05:00:00Z","visits":3,"active":true,"categories":["a"],"bad name":"x"},{"title":"Two","link":"https://example.com/2","published":"","visits":"4","active":false}]""";
            Assert.Contains("The site has no data table 'Feed items'", await FaultOf(Step(DataTableOperation.Upsert, records: Items, match: "link", table: "Feed items")));

            var made = Step(DataTableOperation.Upsert, records: Items, match: "link", table: "Feed items");
            made.CreateIfMissing = new(true);
            IDictionary<string, object> outputs = null!;
            var (count, _) = await ResultOf(made, all => outputs = all);
            Assert.Equal((2, 2), (count, (int)outputs["InsertedCount"]));
            var table = (await PipelineSiteTableStore.FindAsync(KeyValues, Tenant, "feed items", CancellationToken.None))!;
            Assert.Equal(new[] { ("title", PipelineTableColumnType.Text), ("link", PipelineTableColumnType.Text), ("published", PipelineTableColumnType.Date), ("visits", PipelineTableColumnType.Number), ("active", PipelineTableColumnType.Boolean), ("categories", PipelineTableColumnType.Text) },
                table.Columns.Select(column => (column.Name, column.Type)));

            var again = Step(DataTableOperation.Upsert, records: Items, match: "link", table: "Feed items");
            again.CreateIfMissing = new(true);
            (count, _) = await ResultOf(again, all => outputs = all);
            Assert.Equal((2, 0, "[]"), (count, (int)outputs["InsertedCount"], (string)outputs["Inserted"]));
            Assert.Single(await PipelineSiteTableStore.ListAsync(KeyValues, Tenant, CancellationToken.None));
        }

        [Fact]
        public async Task The_step_refuses_what_would_change_a_table_by_mistake_and_leaves_it_as_it_was()
        {
            await SubscribersAsync();
            Assert.Contains("The site has no data table 'Nope'", await FaultOf(Step(DataTableOperation.Get, table: "Nope")));
            Assert.Contains("An Update needs a condition", await FaultOf(Step(DataTableOperation.Update, values: "visits: 1")));
            Assert.Contains("A Delete needs a condition", await FaultOf(Step(DataTableOperation.Delete)));
            Assert.Contains("An Upsert needs its match columns", await FaultOf(Step(DataTableOperation.Upsert, records: """[{"email":"a@example.com"}]""")));
            Assert.Contains("has no column 'name' to match on", await FaultOf(Step(DataTableOperation.Upsert, records: """[{"email":"a@example.com"}]""", match: "name")));

            // A value its column cannot hold fails the step naming both - and the record before it is not written either.
            Assert.Contains("Record 2, visits: 'many' is not a number.", await FaultOf(Step(DataTableOperation.Insert, records: """[{"email":"a@example.com","visits":1},{"email":"b@example.com","visits":"many"}]""")));
            Assert.Empty(await RowsAsync());

            await ResultOf(Step(DataTableOperation.Insert, records: """{"email":"a@example.com","visits":1}"""));
            Assert.Contains("Update needs its values", await FaultOf(Step(DataTableOperation.Update, condition: Rules(("{{ visits }}", "equals", "1")))));
            Assert.Contains("'Subscribers' has no column 'nope'", await FaultOf(Step(DataTableOperation.Update, condition: Rules(("{{ visits }}", "equals", "1")), values: "nope: 2")));
            Assert.Equal("1", Text(Assert.Single(await RowsAsync()), "visits"));
        }
    }
}
