#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - n8n's QuestDB and CrateDB nodes come over as the PostgreSQL connector's SQL, so the PostgreSQL connector is
    /// proved against QuestDB 9.1 and CrateDB 6.0 themselves (this machine's throwaway servers, <see cref="PgWireTestEndpoint"/>)
    /// with the connection the import tells the author to make and the statements it writes: a password that is checked, a
    /// write's values bound, and a read of what was written. QuestDB's connection needs Npgsql's type loading off - without it
    /// every bound value is refused, which is why the import says so, and a test here says so too, so the day it is no longer
    /// true is seen. Fails, never skips, when a server is absent (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class PgWireDatabaseLiveTests(ITestOutputHelper output)
    {
        // The connection the import names for QuestDB: its PostgreSQL port and Npgsql's type loading off.
        private static string QuestDb => $"Host={PgWireTestEndpoint.Host};Port={PgWireTestEndpoint.QuestDbPort};Database=qdb;Server Compatibility Mode=NoTypeLoading";

        private static string CrateDb => $"Host={PgWireTestEndpoint.Host};Port={PgWireTestEndpoint.CrateDbPort};Database=doc";

        private static HostConnectorEgress Egress => new(new[] { PgWireTestEndpoint.Host });

        private static Dictionary<string, string> Credentials(string password) => new() { ["username"] = PgWireTestEndpoint.User, ["password"] = password };

        private static Task<List<ConnectorRecord>> Run(string endpoint, string password, string sql, Dictionary<string, string>? values = null)
        {
            var parameters = new Dictionary<string, string>(values ?? new(), StringComparer.OrdinalIgnoreCase) { ["query"] = sql };
            return Contexts.DrainAsync(new PostgreSqlConnector(), Contexts.Extract(Egress, endpoint, "query", parameters, credentials: Credentials(password), timeoutSeconds: 15));
        }

        [Fact]
        public async Task QuestDB_signs_in_by_its_password_and_refuses_another()
        {
            var right = await new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(Egress, QuestDb, credentials: Credentials(PgWireTestEndpoint.QuestDbPassword), timeoutSeconds: 15));
            output.WriteLine($"[questdb] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            var wrong = await new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(Egress, QuestDb, credentials: Credentials("not-the-password"), timeoutSeconds: 15));
            Assert.False(wrong.Success);
        }

        [Fact]
        public async Task QuestDB_takes_the_imports_insert_bound_and_answers_the_rows_written()
        {
            var password = PgWireTestEndpoint.QuestDbPassword;
            var table = "kassa_" + Guid.NewGuid().ToString("N")[..12];
            await Run(QuestDb, password, $"CREATE TABLE {table} (nummer INT, kund STRING, summa DOUBLE, ts TIMESTAMP) timestamp(ts)");
            try
            {
                // The import's QuestDB insert: its columns quoted, its values bound - no schema, no RETURNING.
                const string Insert = "INSERT INTO \"{0}\" (\"nummer\", \"kund\", \"summa\", \"ts\") VALUES (@nummer, @kund, @summa, now())";
                Assert.Empty(await Run(QuestDb, password, string.Format(Insert, table), new() { ["@nummer"] = "1042", ["@kund"] = "Kafé Ada", ["@summa"] = "99.5" }));
                await Run(QuestDb, password, string.Format(Insert, table), new() { ["@nummer"] = "1043", ["@kund"] = "x'); DROP TABLE nothing; --", ["@summa"] = "12" });

                var rows = await Run(QuestDb, password, $"SELECT nummer, kund, summa FROM \"{table}\" WHERE nummer = @w1", new() { ["@w1"] = "1042" });
                var row = Assert.Single(rows);
                Assert.Equal((1042, "Kafé Ada", 99.5), (row.Fields["nummer"], row.Fields["kund"], row.Fields["summa"]));
                var hostile = Assert.Single(await Run(QuestDb, password, $"SELECT kund FROM \"{table}\" WHERE nummer = @w1", new() { ["@w1"] = "1043" }));
                Assert.Equal("x'); DROP TABLE nothing; --", hostile.Fields["kund"]); // a value, never SQL
            }
            finally
            {
                await Run(QuestDb, password, $"DROP TABLE IF EXISTS {table}");
            }
        }

        [Fact]
        public async Task QuestDB_refuses_a_bound_value_with_type_loading_on_which_is_why_the_import_says_to_turn_it_off()
        {
            // Npgsql's default loads the server's type catalogue, which QuestDB does not keep: a bound value is then refused.
            var endpoint = $"Host={PgWireTestEndpoint.Host};Port={PgWireTestEndpoint.QuestDbPort};Database=qdb";
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => Run(endpoint, PgWireTestEndpoint.QuestDbPassword, "SELECT @n AS n", new() { ["@n"] = "1" }));
            output.WriteLine($"[questdb, type loading on] {refused.GetType().Name}: {refused.Message}");
            Assert.Single(await Run(QuestDb, PgWireTestEndpoint.QuestDbPassword, "SELECT @n AS n", new() { ["@n"] = "1" }));
        }

        [Fact]
        public async Task CrateDB_signs_in_by_its_password_and_refuses_another()
        {
            var right = await new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(Egress, CrateDb, credentials: Credentials(PgWireTestEndpoint.CrateDbPassword), timeoutSeconds: 15));
            output.WriteLine($"[cratedb] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            var wrong = await new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(Egress, CrateDb, credentials: Credentials("not-the-password"), timeoutSeconds: 15));
            Assert.False(wrong.Success); // the server asks for the password - the image's own trusts everyone
        }

        [Fact]
        public async Task CrateDB_takes_the_imports_insert_and_update_and_answers_what_each_wrote()
        {
            var password = PgWireTestEndpoint.CrateDbPassword;
            var table = "kassa_" + Guid.NewGuid().ToString("N")[..12];
            await Run(CrateDb, password, $"CREATE TABLE doc.{table} (nummer INT PRIMARY KEY, kund TEXT, status TEXT)");
            try
            {
                // The import's CrateDB insert, in its schema, and its update by the key - each RETURNING what it wrote.
                var inserted = Assert.Single(await Run(CrateDb, password, $"INSERT INTO \"doc\".\"{table}\" (\"nummer\", \"kund\") VALUES (@nummer, @kund) RETURNING *", new() { ["@nummer"] = "1042", ["@kund"] = "Kafé Ada" }));
                Assert.Equal((1042, "Kafé Ada", null), (inserted.Fields["nummer"], inserted.Fields["kund"], inserted.Fields["status"]));
                var updated = Assert.Single(await Run(CrateDb, password, $"UPDATE \"{table}\" SET \"status\" = @status WHERE \"nummer\" = @nummer RETURNING *", new() { ["@status"] = "x'); DROP TABLE nothing; --", ["@nummer"] = "1042" }));
                Assert.Equal((1042, "x'); DROP TABLE nothing; --"), (updated.Fields["nummer"], updated.Fields["status"]));
                await Run(CrateDb, password, $"REFRESH TABLE doc.{table}");
                var read = Assert.Single(await Run(CrateDb, password, $"SELECT kund FROM doc.{table} WHERE nummer = @w1", new() { ["@w1"] = "1042" }));
                Assert.Equal("Kafé Ada", read.Fields["kund"]);
            }
            finally
            {
                await Run(CrateDb, password, $"DROP TABLE IF EXISTS doc.{table}");
            }
        }
    }
}
