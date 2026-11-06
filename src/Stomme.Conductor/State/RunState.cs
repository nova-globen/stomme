using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stomme.Conductor.Models;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

namespace Stomme.Conductor.State;

/// <summary>One input of a run as the host lists it: a key (a path, a name), a hash of its content, and its size.</summary>
public sealed record InputRecord(string Key, string Hash, long Size);

/// <summary>What a run found when it compared the host's inputs with the state (ADR 0017).</summary>
public sealed record InputDiff(int Unchanged, int Changed, int Added, int Gone, IReadOnlyList<string> GoneKeys)
{
    public bool IsFirstRun => Unchanged == 0 && Changed == 0 && Gone == 0;
}

/// <summary>A unit of work's row: done or not, from which input, with what result.</summary>
public sealed record UnitRow(string Phase, string Key, string InputHash, string Status, string RunId, decimal Usd, string? Result);

/// <summary>One glossary entry: the reader's working memory, bounded and pruned by rule (ADR 0017).</summary>
public sealed record GlossaryEntry(long Id, string Kind, string Term, string Meaning, string? Source, string AddedRun, DateTimeOffset Added, DateTimeOffset LastUsed, int Uses);

/// <summary>An open or answered question as the state keeps it (ADR 0007, 0017).</summary>
public sealed record QuestionRow(Question Question, string AskedRun, DateTimeOffset Asked, string? Answer, string? AnsweredBy);

/// <summary>
/// The run state (ADR 0017): one SQLite database per output folder, the source of truth a run resumes from and
/// a later run diffs against. It holds the inputs by key and hash, the records a host keeps (its map, its
/// catalogue), the glossary, the questions and answers, the index of the reply cache, the runs with their
/// spend, and the progress table of units of work. Every unit completes in one transaction with everything it
/// produced. One connection, one lock: SQLite has one writer, and the phases complete units from several
/// threads. A state written by an earlier schema is refused, never migrated silently; a fresh start keeps it renamed.
/// </summary>
public sealed class RunState : IDisposable
{
    private const int SchemaVersion = 2;
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    private RunState(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
    }

    public string Path { get; }

    public static RunState Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // No pooling: the one connection owns the database, and a fresh start must be able to rename it right after a close.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        var state = new RunState(path, connection);
        state.Execute("PRAGMA journal_mode=WAL;");
        state.Execute("PRAGMA synchronous=NORMAL;");
        state.Execute("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        var version = state.Scalar("SELECT value FROM meta WHERE key = 'schema_version'") as string;
        var current = SchemaVersion.ToString(CultureInfo.InvariantCulture);
        if (version is not null && version != current)
        {
            state.Dispose();
            throw new InvalidOperationException(
                $"The state at {path} has schema version {version}; this engine writes version {current}. Start over with a fresh state (ConductorOptions.Fresh, a console's --fresh): the old state is kept renamed.");
        }

        state.CreateSchema();
        if (version is null)
        {
            state.Execute("INSERT INTO meta (key, value) VALUES ('schema_version', $v)", ("$v", current));
        }

        return state;
    }

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY, started TEXT NOT NULL, finished TEXT, outcome TEXT, message TEXT,
                source TEXT, source_version TEXT, source_note TEXT, playbook_version TEXT, playbook_hash TEXT,
                usd REAL NOT NULL DEFAULT 0, calls INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS inputs (
                key TEXT PRIMARY KEY, hash TEXT NOT NULL, size INTEGER NOT NULL, status TEXT NOT NULL,
                first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, changed_in TEXT);
            CREATE TABLE IF NOT EXISTS units (
                phase TEXT NOT NULL, key TEXT NOT NULL, input_hash TEXT NOT NULL, status TEXT NOT NULL, run_id TEXT NOT NULL,
                started TEXT, finished TEXT, usd REAL NOT NULL DEFAULT 0, result TEXT, PRIMARY KEY (phase, key));
            CREATE TABLE IF NOT EXISTS records (
                kind TEXT NOT NULL, key TEXT NOT NULL, json TEXT NOT NULL, run_id TEXT NOT NULL, updated TEXT NOT NULL, PRIMARY KEY (kind, key));
            CREATE TABLE IF NOT EXISTS glossary (
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, term TEXT NOT NULL, meaning TEXT NOT NULL, source TEXT,
                added_run TEXT NOT NULL, added TEXT NOT NULL, last_used TEXT NOT NULL, uses INTEGER NOT NULL DEFAULT 0, UNIQUE (kind, term));
            CREATE TABLE IF NOT EXISTS questions (
                key TEXT PRIMARY KEY, phase TEXT NOT NULL, text TEXT NOT NULL, options TEXT NOT NULL, default_answer TEXT NOT NULL,
                context TEXT, blocking INTEGER NOT NULL, asked_run TEXT NOT NULL, asked TEXT NOT NULL,
                answer TEXT, answered_by TEXT, answered TEXT);
            CREATE TABLE IF NOT EXISTS cache_index (
                hash TEXT PRIMARY KEY, kind TEXT NOT NULL, model TEXT NOT NULL, answered_by TEXT, at TEXT NOT NULL, purpose TEXT, usd REAL NOT NULL);
            """);
    }

    // ----- runs -----

    public void BeginRun(string runId, string playbookVersion, string playbookHash)
    {
        Execute("INSERT OR REPLACE INTO runs (run_id, started, playbook_version, playbook_hash) VALUES ($id, $started, $pv, $ph)",
            ("$id", runId), ("$started", Now()), ("$pv", playbookVersion), ("$ph", playbookHash));
    }

    /// <summary>What the host read this run: a path or a name, a version such as a commit, and a note (ADR 0009: the run records what it read).</summary>
    public void SetRunSource(string runId, string source, string? version, string? note)
    {
        Execute("UPDATE runs SET source = $s, source_version = $v, source_note = $n WHERE run_id = $id",
            ("$s", source), ("$v", version), ("$n", note), ("$id", runId));
    }

    public void EndRun(string runId, string outcome, string message, decimal usd, int calls)
    {
        Execute("UPDATE runs SET finished = $f, outcome = $o, message = $m, usd = $usd, calls = $calls WHERE run_id = $id",
            ("$f", Now()), ("$o", outcome), ("$m", message), ("$usd", (double)usd), ("$calls", calls), ("$id", runId));
    }

    public int RunCount => (int)(long)(Scalar("SELECT COUNT(*) FROM runs") ?? 0L);

    public decimal AllRunsUsd => (decimal)(double)(Scalar("SELECT COALESCE(SUM(usd), 0) FROM runs") ?? 0.0);

    // ----- inputs (what the host reads, by key and hash; the diff of a later run) -----

    /// <summary>Compares the host's inputs with what the state knew (ADR 0017): unchanged kept, changed or new recorded, gone marked and never deleted.</summary>
    public InputDiff ReconcileInputs(IEnumerable<InputRecord> inputs, string runId)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var list = inputs.ToList();
        lock (_gate)
        {
            var known = new Dictionary<string, (string Hash, string Status)>(StringComparer.Ordinal);
            using (var read = _connection.CreateCommand())
            {
                read.CommandText = "SELECT key, hash, status FROM inputs";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    known[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
                }
            }

            int unchanged = 0, changed = 0, added = 0;
            var now = Now();
            using var transaction = _connection.BeginTransaction();
            foreach (var input in list)
            {
                if (known.TryGetValue(input.Key, out var old))
                {
                    if (old.Hash == input.Hash && old.Status == "present")
                    {
                        unchanged++;
                        Execute(transaction, "UPDATE inputs SET last_seen = $now, size = $size WHERE key = $key",
                            ("$now", now), ("$size", input.Size), ("$key", input.Key));
                    }
                    else
                    {
                        changed++;
                        Execute(transaction, "UPDATE inputs SET hash = $hash, size = $size, status = 'present', last_seen = $now, changed_in = $run WHERE key = $key",
                            ("$hash", input.Hash), ("$size", input.Size), ("$now", now), ("$run", runId), ("$key", input.Key));
                    }
                }
                else
                {
                    added++;
                    Execute(transaction, """
                        INSERT INTO inputs (key, hash, size, status, first_seen, last_seen, changed_in)
                        VALUES ($key, $hash, $size, 'present', $now, $now, $run)
                        """,
                        ("$key", input.Key), ("$hash", input.Hash), ("$size", input.Size), ("$now", now), ("$run", runId));
                }
            }

            var present = list.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            var gone = new List<string>();
            foreach (var (key, (_, status)) in known)
            {
                if (!present.Contains(key) && status == "present")
                {
                    gone.Add(key);
                    Execute(transaction, "UPDATE inputs SET status = 'gone', changed_in = $run WHERE key = $key", ("$run", runId), ("$key", key));
                }
            }

            transaction.Commit();
            var stillGone = known.Count(k => k.Value.Status == "gone" && !present.Contains(k.Key));
            return new InputDiff(unchanged, changed, added, gone.Count + stillGone, gone);
        }
    }

    /// <summary>The keys of inputs that were there once and are gone now, kept marked (ADR 0017).</summary>
    public IReadOnlyList<string> GoneInputs()
    {
        lock (_gate)
        {
            var keys = new List<string>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT key FROM inputs WHERE status = 'gone' ORDER BY key";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                keys.Add(reader.GetString(0));
            }

            return keys;
        }
    }

    // ----- units of work -----

    /// <summary>The result of a unit done from this input, or null when it is not done or was done from a different input.</summary>
    public UnitRow? DoneUnit(string phase, string key, string inputHash)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT input_hash, status, run_id, usd, result FROM units WHERE phase = $p AND key = $k";
            command.Parameters.AddWithValue("$p", phase);
            command.Parameters.AddWithValue("$k", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var row = new UnitRow(phase, key, reader.GetString(0), reader.GetString(1), reader.GetString(2),
                (decimal)reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetString(4));
            return row.Status == "done" && row.InputHash == inputHash ? row : null;
        }
    }

    /// <summary>Completes units in one transaction, each with what it produced (ADR 0017).</summary>
    public void CompleteUnits(string phase, string runId, IEnumerable<(string Key, string InputHash, decimal Usd, object? Result)> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var now = Now();
            foreach (var (key, inputHash, usd, result) in units)
            {
                Execute(transaction, """
                    INSERT OR REPLACE INTO units (phase, key, input_hash, status, run_id, started, finished, usd, result)
                    VALUES ($p, $k, $h, 'done', $run, $now, $now, $usd, $result)
                    """,
                    ("$p", phase), ("$k", key), ("$h", inputHash), ("$run", runId), ("$now", now), ("$usd", (double)usd),
                    ("$result", result is null ? null : JsonSerializer.Serialize(result, RunContext.JsonOptions)));
            }

            transaction.Commit();
        }
    }

    public void CompleteUnit(string phase, string runId, string key, string inputHash, decimal usd, object? result) =>
        CompleteUnits(phase, runId, [(key, inputHash, usd, result)]);

    public IReadOnlyList<UnitRow> UnitsOf(string phase)
    {
        lock (_gate)
        {
            var rows = new List<UnitRow>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT key, input_hash, status, run_id, usd, result FROM units WHERE phase = $p ORDER BY key";
            command.Parameters.AddWithValue("$p", phase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new UnitRow(phase, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    (decimal)reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return rows;
        }
    }

    /// <summary>Forgets every unit of work whose key starts with the prefix, in any phase, so it runs again.</summary>
    public void ForgetUnitsByPrefix(string keyPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);
        Execute("DELETE FROM units WHERE substr(key, 1, length($p)) = $p", ("$p", keyPrefix));
    }

    public void ForgetUnits(string? phase)
    {
        if (phase is null)
        {
            Execute("DELETE FROM units");
        }
        else
        {
            Execute("DELETE FROM units WHERE phase = $p", ("$p", phase));
        }
    }

    public IReadOnlyDictionary<string, (int Done, decimal Usd)> UnitCounts()
    {
        lock (_gate)
        {
            var counts = new Dictionary<string, (int, decimal)>(StringComparer.Ordinal);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT phase, COUNT(*), COALESCE(SUM(usd), 0) FROM units WHERE status = 'done' GROUP BY phase";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counts[reader.GetString(0)] = ((int)reader.GetInt64(1), (decimal)reader.GetDouble(2));
            }

            return counts;
        }
    }

    // ----- records (what a host keeps in the state: its map, its catalogue, by kind and key) -----

    public void SaveRecord(string kind, string key, string json, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(json);
        Execute("INSERT OR REPLACE INTO records (kind, key, json, run_id, updated) VALUES ($kind, $key, $json, $run, $now)",
            ("$kind", kind), ("$key", key), ("$json", json), ("$run", runId), ("$now", Now()));
    }

    public void DeleteRecord(string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Execute("DELETE FROM records WHERE kind = $kind AND key = $key", ("$kind", kind), ("$key", key));
    }

    public string? LoadRecord(string kind, string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT json FROM records WHERE kind = $kind AND key = $key";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public IReadOnlyList<(string Key, string Json)> Records(string kind)
    {
        lock (_gate)
        {
            var rows = new List<(string, string)>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT key, json FROM records WHERE kind = $kind ORDER BY key";
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            return rows;
        }
    }

    // ----- the glossary -----

    public void UpsertGlossary(string kind, string term, string meaning, string? source, string runId)
    {
        var now = Now();
        Execute("""
            INSERT INTO glossary (kind, term, meaning, source, added_run, added, last_used, uses)
            VALUES ($kind, $term, $meaning, $source, $run, $now, $now, 0)
            ON CONFLICT (kind, term) DO UPDATE SET meaning = excluded.meaning, source = COALESCE(excluded.source, glossary.source), last_used = excluded.last_used
            """,
            ("$kind", kind), ("$term", term), ("$meaning", meaning), ("$source", source), ("$run", runId), ("$now", now));
    }

    public void TouchGlossary(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var now = Now();
            foreach (var id in ids)
            {
                Execute(transaction, "UPDATE glossary SET uses = uses + 1, last_used = $now WHERE id = $id", ("$now", now), ("$id", id));
            }

            transaction.Commit();
        }
    }

    public void RemoveGlossary(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var id in ids)
            {
                Execute(transaction, "DELETE FROM glossary WHERE id = $id", ("$id", id));
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<GlossaryEntry> Glossary()
    {
        lock (_gate)
        {
            var entries = new List<GlossaryEntry>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, kind, term, meaning, source, added_run, added, last_used, uses FROM glossary ORDER BY kind, term";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new GlossaryEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), Parse(reader.GetString(6)), Parse(reader.GetString(7)), (int)reader.GetInt64(8)));
            }

            return entries;
        }
    }

    // ----- questions -----

    public void UpsertQuestion(Question question, string runId, Answer? answer)
    {
        ArgumentNullException.ThrowIfNull(question);
        var now = Now();
        Execute("""
            INSERT INTO questions (key, phase, text, options, default_answer, context, blocking, asked_run, asked, answer, answered_by, answered)
            VALUES ($key, $phase, $text, $options, $default, $context, $blocking, $run, $now, $answer, $by, $answered)
            ON CONFLICT (key) DO UPDATE SET phase = excluded.phase, text = excluded.text, options = excluded.options,
                default_answer = excluded.default_answer, context = excluded.context, blocking = excluded.blocking,
                asked_run = excluded.asked_run, asked = excluded.asked,
                answer = COALESCE(excluded.answer, questions.answer), answered_by = COALESCE(excluded.answered_by, questions.answered_by),
                answered = COALESCE(excluded.answered, questions.answered)
            """,
            ("$key", question.Key), ("$phase", question.Phase), ("$text", question.Text),
            ("$options", JsonSerializer.Serialize(question.Options, RunContext.JsonOptions)), ("$default", question.DefaultIfUnanswered),
            ("$context", question.Context), ("$blocking", question.Blocking ? 1 : 0), ("$run", runId), ("$now", now),
            ("$answer", answer?.Text), ("$by", answer?.By), ("$answered", answer is null ? null : now));
    }

    /// <summary>Removes an open question that a later judgment settled; an answered one stays. True when one was removed.</summary>
    public bool WithdrawQuestion(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM questions WHERE key = $k AND answer IS NULL";
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<QuestionRow> Questions()
    {
        lock (_gate)
        {
            var rows = new List<QuestionRow>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT key, phase, text, options, default_answer, context, blocking, asked_run, asked, answer, answered_by FROM questions ORDER BY asked, key";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var options = JsonSerializer.Deserialize<List<QuestionOption>>(reader.GetString(3), RunContext.JsonOptions) ?? [];
                var question = new Question(reader.GetString(0), reader.GetString(1), reader.GetString(2), options, reader.GetString(4))
                {
                    Context = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Blocking = reader.GetInt64(6) == 1,
                };
                rows.Add(new QuestionRow(question, reader.GetString(7), Parse(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
            }

            return rows;
        }
    }

    // ----- the cache index -----

    public void IndexCache(CachedReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        Execute("INSERT OR REPLACE INTO cache_index (hash, kind, model, answered_by, at, purpose, usd) VALUES ($h, $k, $m, $a, $at, $p, $usd)",
            ("$h", reply.Hash), ("$k", reply.Kind), ("$m", reply.Model), ("$a", reply.AnsweredBy), ("$at", reply.At.ToString("O", CultureInfo.InvariantCulture)),
            ("$p", reply.RequestSummary), ("$usd", (double)reply.Usd));
    }

    // ----- counts, for state.md and a host's own sections -----

    public long Count(string table) => (long)(Scalar($"SELECT COUNT(*) FROM {table}") ?? 0L);

    public long CountWhere(string table, string where) => (long)(Scalar($"SELECT COUNT(*) FROM {table} WHERE {where}") ?? 0L);

    // ----- plumbing -----

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            Execute(null, sql, parameters);
        }
    }

    private void Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value is DBNull ? null : value;
        }
    }

    private static string Now() => DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
