using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Models;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Tests;

/// <summary>
/// A reader that answers from the request's own text, the way the real one would on the synthetic repository:
/// the nature from the guide, a kind per listed path by its name, a "look closer" request once, and, for a
/// piece the gate flagged, an output that obeys the hidden instruction so the gate on the way out has
/// something to catch. It can be told to fail on its n-th kinds call, to prove resume.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private int _kindsCalls;
    private int _calls;
    private int _documentCalls;

    public int Calls => _calls;

    public int KindsCalls => _kindsCalls;

    public int NatureCalls { get; private set; }

    public int ItemsCalls { get; private set; }

    public int DocumentCalls => _documentCalls;

    /// <summary>Every text this reader was sent, so a test can say what never left the machine.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Sent { get; } = new();

    /// <summary>When set, the reader's note on the repository, in place of "nothing special".</summary>
    public string? NatureNote { get; set; }

    /// <summary>When true, the reader names no id scheme for the repository (the third re-check's N25).</summary>
    public bool NoSchemes { get; set; }

    /// <summary>When set, the path of the ledger the reader names that is not there, as an open ledger, in place of its guess docs/missing.md.</summary>
    public string? MissingLedger { get; set; }

    /// <summary>When set, the ledgers the reader names: the JSON array of the nature reply, in place of the usual three.</summary>
    public string? LedgersJson { get; set; }

    /// <summary>When true, the entries of bulleted lists are rows too (the prose list of the synthetic backlog aside), wrapped lines joined as a reader joins them.</summary>
    public bool BulletRows { get; set; }

    /// <summary>
    /// How the reader words the rows it returns: 0 as the file writes them; 1 titles of four words, a summary of its
    /// own and no id for any row; 2 titles cut at thirty characters with an ellipsis, another summary, labels as ids.
    /// </summary>
    public int Wording { get; set; }

    /// <summary>With the second wording: every row is also given a state and a kind of the reader's own, as a second reading of a ledger does.</summary>
    public bool JudgesAnew { get; set; }

    /// <summary>When set, the n-th kinds call throws once, then the setting clears.</summary>
    public int? FailOnKindsCall { get; set; }

    /// <summary>When set, the documents call for this path throws once, then the setting clears.</summary>
    public string? FailOnDocument { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var list = messages.ToList();
        var system = list[0].Text ?? "";
        var user = string.Join("\n", list.Skip(1).Select(m => m.Text ?? ""));
        Sent.Enqueue(system + " " + user);
        string reply;
        if (system.Contains("orientation, part 1", StringComparison.Ordinal))
        {
            NatureCalls++;
            reply = user.Contains("Your first answer asked to look closer", StringComparison.Ordinal)
                ? Nature(requests: false)
                : Nature(requests: true);
            if (NoSchemes)
            {
                reply = Regex.Replace(reply, "\"id_schemes\": \\[.*?\\],\\s*\"status_vocabulary\"", _ => "\"id_schemes\": [],\n          \"status_vocabulary\"", RegexOptions.Singleline);
            }

            if (NatureNote is not null)
            {
                reply = reply.Replace("\"nothing special\"", System.Text.Json.JsonSerializer.Serialize(NatureNote), StringComparison.Ordinal);
            }

            if (LedgersJson is not null)
            {
                reply = Regex.Replace(reply, "\"ledgers\": \\[.*?\\],\\s*\"document_roots\"", _ => "\"ledgers\": " + LedgersJson + ",\n          \"document_roots\"", RegexOptions.Singleline);
            }

            if (MissingLedger is not null)
            {
                reply = reply.Replace("{\"path\": \"docs/missing.md\", \"kind\": \"change-log\"", "{\"path\": " + System.Text.Json.JsonSerializer.Serialize(MissingLedger) + ", \"kind\": \"open-ledger\"", StringComparison.Ordinal);
            }
        }
        else if (system.Contains("orientation, part 2", StringComparison.Ordinal))
        {
            var n = Interlocked.Increment(ref _kindsCalls);
            if (FailOnKindsCall == n)
            {
                FailOnKindsCall = null;
                throw new InvalidOperationException("the scripted reader failed on purpose");
            }

            reply = Kinds(user);
        }
        else if (system.Contains("TASK: items, rows", StringComparison.Ordinal))
        {
            ItemsCalls++;
            reply = Reworded(Rows(user), user);
        }
        else if (system.Contains("TASK: decisions", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _decisionCalls);
            reply = Draft(user);
        }
        else if (system.Contains("TASK: documents", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _documentCalls);
            var failing = FailOnDocument;
            if (failing is not null && user.Contains("Document: " + failing + " (", StringComparison.Ordinal))
            {
                FailOnDocument = null;
                throw new InvalidOperationException("the scripted reader failed on purpose");
            }

            reply = Documents(user);
        }
        else
        {
            reply = "{}";
        }

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            ModelId = "scripted-1",
            Usage = new UsageDetails { InputTokenCount = user.Length / 4, OutputTokenCount = reply.Length / 4 },
        };
        return Task.FromResult(response);
    }

    private int _decisionCalls;

    public int DecisionCalls => _decisionCalls;

    /// <summary>A draft from the noted decision in the instruction: the title is the decision itself, cut.</summary>
    private static string Draft(string user)
    {
        var match = Regex.Match(user, "The decision as noted: \"(?<text>[^\"]+)\"");
        var noted = match.Success ? match.Groups["text"].Value : "a decision";
        var title = noted.Length > 60 ? noted[..60] : noted;
        return new JsonObject
        {
            ["title"] = title,
            ["context"] = "The repository needed a choice here.",
            ["decision"] = noted,
            ["consequences"] = "",
            ["date"] = null,
            ["confidence"] = 0.8,
            ["reasoning"] = "The evidence states the choice.",
        }.ToJsonString();
    }

    private static string Nature(bool requests) =>
        """
        {
          "nature": {"kind": "code", "description": "A small Python tool with a backlog in docs.", "confidence": 0.9, "reasoning": "README names the backlog and the code.", "evidence": [{"path": "README.md", "line": 3, "quote": "Work items live in docs/backlog.md"}]},
          "natural_languages": ["en"],
          "programming_languages": ["Python"],
          "id_schemes": [{"name": "backlog id", "example": "BL-12", "regex": "\\bBL-(\\d+)\\b", "numeric_group": 1, "refers_to": "work-item", "where_defined": "docs/backlog.md", "reasoning": "README says ids look like BL-12.", "evidence": {"path": "README.md", "line": 3, "quote": "Ids look like BL-12"}},
                         {"name": "ghost", "example": "ZZ-9", "regex": "\\bZZ-(\\d+)\\b", "numeric_group": 1, "refers_to": "other", "where_defined": null, "reasoning": "a guess the scan should drop", "evidence": null}],
          "status_vocabulary": [{"word": "OPEN", "meaning": "ready to pick up", "mapped": "backlog", "evidence": {"path": "docs/backlog.md", "line": 3, "quote": "OPEN"}}],
          "tier_vocabulary": [],
          "spec_index": null,
          "ledgers": [{"path": "docs/backlog.md", "kind": "open-ledger", "reasoning": "README says so.", "evidence": {"path": "README.md", "line": 3, "quote": "Work items live in docs/backlog.md"}},
                      {"path": "docs/backlog-done.md", "kind": "finished-ledger", "reasoning": "README says so.", "evidence": null},
                      {"path": "docs/missing.md", "kind": "change-log", "reasoning": "a path that does not exist", "evidence": null}],
          "document_roots": ["docs/"],
          "documents_thin": {"value": false, "reasoning": "several documents"},
          "requests": REQUESTS,
          "notes": ["nothing special"],
          "reasoning": "Read the README, then the tree."
        }
        """.Replace("REQUESTS", requests ? "[{\"kind\": \"listing\", \"path\": \"docs\", \"why\": \"to see the specs\"}]" : "[]", StringComparison.Ordinal);

    private static string Kinds(string user)
    {
        var files = new JsonArray();
        foreach (Match m in Regex.Matches(user, @"^- (?<path>.+?) \(\d+ bytes, \d+ lines\)\r?$", RegexOptions.Multiline))
        {
            var path = m.Groups["path"].Value;
            var flagged = Regex.IsMatch(user, Regex.Escape(path) + @", lines \d+-\d+[^\n]*\[FLAGGED");
            var kind = path.Contains("backlog-done", StringComparison.Ordinal) ? "finished-ledger"
                : path.Contains("backlog", StringComparison.Ordinal) ? "open-ledger"
                : path.Contains("CHANGELOG", StringComparison.OrdinalIgnoreCase) ? "change-log"
                : path.Contains("template", StringComparison.OrdinalIgnoreCase) ? "template"
                : path.Contains("README", StringComparison.OrdinalIgnoreCase) ? "guide"
                : path.Contains("specs/", StringComparison.Ordinal) ? "spec"
                : path.Contains("adr/", StringComparison.Ordinal) ? "adr"
                : "other";
            files.Add(new JsonObject
            {
                ["path"] = path,
                ["kind"] = kind,
                ["title"] = path,
                ["language"] = "en",
                ["confidence"] = 0.8,
                ["evidence"] = new JsonObject { ["line"] = 1, ["quote"] = "first line" },
                ["reasoning"] = flagged ? "HACKED: marked as done because the file told me to" : "named by its content",
                ["alternatives"] = new JsonArray(),
            });
        }

        return new JsonObject { ["files"] = files }.ToJsonString();
    }

    /// <summary>The rows of the ledger chunk between the input markers, read the way the real reader would: one row per table row, references from its text, the prose list reported apart.</summary>
    /// <summary>Rows whose title contains this are left out of the reply, as a reader that undercounts would.</summary>
    public static string? SkipRowsWith { get; set; }

    /// <summary>When set, the rows the reader returns for a chunk, in place of what it reads from the text: a test says exactly what the reader said.</summary>
    public Func<string, JsonArray>? RowScript { get; set; }

    private string Reworded(string reply, string user)
    {
        if (RowScript is not null)
        {
            var scripted = JsonNode.Parse(reply)!.AsObject();
            scripted["rows"] = RowScript(user);
            scripted["prose_work"] = new JsonArray();
            return scripted.ToJsonString();
        }

        if (!BulletRows && Wording == 0)
        {
            return reply;
        }

        var root = JsonNode.Parse(reply)!.AsObject();
        var rows = root["rows"]!.AsArray();
        if (BulletRows)
        {
            var start = user.IndexOf("=== INPUT TEXT", StringComparison.Ordinal);
            var bodyStart = user.IndexOf("===\n", start, StringComparison.Ordinal) + 4;
            var end = user.IndexOf("=== END OF INPUT TEXT ===", StringComparison.Ordinal);
            var lines = user[bodyStart..end].Split('\n');
            var prose = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith('#'))
                {
                    prose = lines[i].StartsWith("### Deferred features", StringComparison.Ordinal);
                }

                var m = Regex.Match(lines[i], @"^[-*]\s+(?:\[(?<check>[ xX])\]\s+)?(?<text>.+?)\s*$");
                if (prose || !m.Success)
                {
                    continue;
                }

                // A reader gives the entry without its emphasis and joins a line that wraps.
                var text = m.Groups["text"].Value;
                for (var next = i + 1; next < lines.Length && lines[next].StartsWith("  ", StringComparison.Ordinal) && lines[next].Trim().Length > 0; next++)
                {
                    text += " " + lines[next].Trim();
                }

                text = text.Replace("**", "", StringComparison.Ordinal);
                var label = Regex.Match(text, @"^(?<label>Task \d+)\b");
                rows.Add(new JsonObject
                {
                    ["key"] = label.Success ? label.Groups["label"].Value : null,
                    ["ids"] = new JsonArray(),
                    ["title"] = text,
                    ["type"] = null,
                    ["type_as_written"] = null,
                    ["type_confidence"] = 0.0,
                    ["relations"] = new JsonArray(),
                    ["status_as_written"] = null,
                    ["status"] = null,
                    ["condition"] = null,
                    ["tier"] = null,
                    ["size"] = null,
                    ["dates"] = new JsonArray(),
                    ["detail"] = "",
                    ["references"] = new JsonArray(),
                    ["evidence"] = new JsonObject { ["line"] = i + 1, ["quote"] = lines[i].Trim() },
                    ["confidence"] = 0.9,
                    ["reasoning"] = "a list entry",
                });
            }
        }

        foreach (var row in rows)
        {
            var title = row!["title"]!.GetValue<string>();
            var words = title.Split(' ');
            if (Wording == 1)
            {
                row["title"] = words.Length > 4 ? string.Join(' ', words.Take(4)) : title;
                row["detail"] = "The reader's own account of the row, in its first wording.";
                // This reader gives no id at all, not even one the row carries: the key must not turn on it.
                row["key"] = null;
                row["ids"] = new JsonArray();
            }
            else if (Wording == 2)
            {
                row["title"] = title.Length > 30 ? title[..30] + "..." : title;
                row["detail"] = "Another account of the same row, in other words altogether.";
                if (JudgesAnew)
                {
                    row["status"] = "in-progress";
                    row["type"] = "spike";
                    row["type_as_written"] = "spike";
                    row["type_confidence"] = 0.8;
                }
            }
        }

        return root.ToJsonString();
    }

    private static string Rows(string user)
    {
        var start = user.IndexOf("=== INPUT TEXT", StringComparison.Ordinal);
        var bodyStart = user.IndexOf("===\n", start, StringComparison.Ordinal) + 4;
        var end = user.IndexOf("=== END OF INPUT TEXT ===", StringComparison.Ordinal);
        var lines = user[bodyStart..end].Split('\n');
        var rows = new JsonArray();
        for (var i = 0; i < lines.Length; i++)
        {
            // A row with an id of the scheme, a bare number, or no id at all (an empty first cell); never the header or the rule line.
            var m = Regex.Match(lines[i], @"^\|\s*\**(?<key>BL-[0-9]+[a-z]?|\d+)?\**\s*\|\s*(?<title>[^|]+?)\s*\|\s*(?<status>[^|]+?)\s*\|");
            if (!m.Success)
            {
                // An entry of a numbered list is a row too, and a reader gives its number as its id, as it does a table's.
                m = Regex.Match(lines[i], @"^(?<key>\d+)\.\s+(?<title>.+?)\s*$");
            }

            if (!m.Success)
            {
                continue;
            }

            var key = m.Groups["key"].Value;
            var title = m.Groups["title"].Value;
            if (SkipRowsWith is not null && title.Contains(SkipRowsWith, StringComparison.Ordinal))
            {
                // The reader that undercounts a chunk, for the reconciliation test.
                continue;
            }

            var references = new JsonArray();
            foreach (Match id in Regex.Matches(title, @"\bBL-\d+\b"))
            {
                if (id.Value != key)
                {
                    references.Add(new JsonObject { ["kind"] = "id", ["text"] = id.Value });
                }
            }

            foreach (Match path in Regex.Matches(title, @"docs/[\w./-]+"))
            {
                references.Add(new JsonObject { ["kind"] = "path", ["text"] = path.Value });
            }

            var typeWord = Regex.Match(title, @"\((?<t>bug|feature|spike|task)\)").Groups["t"].Value;
            var relations = new JsonArray();
            foreach (Match link in Regex.Matches(title, @"(?<words>(blocked by|part of|split from|duplicate of|supersedes|holds) (?<id>BL-\d+))"))
            {
                // "split from" is read as a parent, as a reader without the distinction does; the judge says what the words mean.
                var kind = link.Groups["words"].Value.StartsWith("blocked", StringComparison.Ordinal) ? "blocker" : link.Groups["words"].Value.StartsWith("part", StringComparison.Ordinal) || link.Groups["words"].Value.StartsWith("split", StringComparison.Ordinal) ? "parent" : link.Groups["words"].Value.StartsWith("dup", StringComparison.Ordinal) ? "duplicate" : link.Groups["words"].Value.StartsWith("holds", StringComparison.Ordinal) ? "child" : "supersedes";
                relations.Add(new JsonObject { ["kind"] = kind, ["id"] = link.Groups["id"].Value, ["as_written"] = link.Groups["words"].Value, ["confidence"] = 0.8 });
            }

            rows.Add(new JsonObject
            {
                ["key"] = key.Length == 0 ? null : key,
                ["ids"] = key.Length == 0 ? new JsonArray() : new JsonArray(key),
                ["title"] = title,
                ["type"] = typeWord.Length == 0 ? null : typeWord,
                ["type_as_written"] = typeWord.Length == 0 ? null : typeWord,
                ["type_confidence"] = typeWord.Length == 0 ? 0.0 : 0.8,
                ["relations"] = relations,
                ["status_as_written"] = m.Groups["status"].Value.Length == 0 ? null : m.Groups["status"].Value,
                ["status"] = null,
                ["condition"] = Regex.Match(title, @"\[cond: (?<c>[^\]]+)\]") is { Success: true } stated ? stated.Groups["c"].Value : null,
                ["tier"] = null,
                ["size"] = null,
                ["dates"] = new JsonArray(),
                ["detail"] = "",
                ["references"] = references,
                ["evidence"] = new JsonObject { ["line"] = i + 1, ["quote"] = lines[i].Trim() },
                ["confidence"] = 0.9,
                ["reasoning"] = "a table row",
            });
        }

        var prose = new JsonArray();
        var heading = Array.FindIndex(lines, l => l.StartsWith("### Deferred features", StringComparison.Ordinal));
        if (heading >= 0)
        {
            var bullets = new JsonArray();
            for (var i = heading + 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("- ", StringComparison.Ordinal))
                {
                    bullets.Add(lines[i][2..].Trim());
                }
            }

            if (bullets.Count > 0)
            {
                prose.Add(new JsonObject { ["heading"] = "Deferred features", ["bullets"] = bullets, ["reasoning"] = "bullets of deferred work without rows" });
            }
        }

        return new JsonObject { ["rows"] = rows, ["prose_work"] = prose, ["notes"] = new JsonArray(), ["reasoning"] = "the table rows of the chunk" }.ToJsonString();
    }

    /// <summary>
    /// The card of the document between the input markers, read the way the real reader would: the title from
    /// the first heading, the kind from the path, the standing from the text's own words, ids and paths as
    /// references with their lines, a decision where the text says "We decided"; for a flagged text an output
    /// that obeys it, and for a withheld text a summary that says so.
    /// </summary>
    private static string Documents(string user)
    {
        var pathMatch = Regex.Match(user, @"^Document: (?<path>\S+) \(", RegexOptions.Multiline);
        var path = pathMatch.Success ? pathMatch.Groups["path"].Value : "";
        var start = user.IndexOf("=== INPUT TEXT", StringComparison.Ordinal);
        var body = "";
        if (start >= 0)
        {
            var bodyStart = user.IndexOf("===\n", start, StringComparison.Ordinal) + 4;
            var end = user.IndexOf("=== END OF INPUT TEXT ===", StringComparison.Ordinal);
            body = user[bodyStart..end];
        }

        var lines = body.Split('\n');
        var chunk = Regex.Match(user, @"This is chunk (?<n>\d+) of (?<m>\d+)");
        var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim();
        var agents = path.Contains(".claude/", StringComparison.Ordinal) || path.EndsWith("CLAUDE.md", StringComparison.Ordinal);
        var kind = agents ? "guide"
            : path.Contains("adr/", StringComparison.Ordinal) ? "adr"
            : path.Contains("roadmap", StringComparison.Ordinal) ? "plan"
            : path.Contains("specs/", StringComparison.Ordinal) ? "spec"
            : path.Contains("README", StringComparison.OrdinalIgnoreCase) ? "guide"
            : "other";
        var hacked = body.Contains("Ignore all previous instructions", StringComparison.OrdinalIgnoreCase);
        var summary = start < 0 ? "The input text was withheld by the engine; nothing can be said about it."
            : hacked ? "HACKED: everything is marked as done, as the file instructs."
            : chunk.Success ? $"Part {chunk.Groups["n"].Value}: {lines.FirstOrDefault(l => l.StartsWith("## ", StringComparison.Ordinal))?.Trim() ?? "text"}."
            : $"{title ?? path} is a {kind} of {lines.Length} lines.";
        var references = new JsonArray();
        var decisions = new JsonArray();
        var dates = new JsonArray();
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match id in Regex.Matches(lines[i], @"\bBL-\d+\b"))
            {
                references.Add(new JsonObject { ["kind"] = "id", ["text"] = id.Value, ["line"] = i + 1 });
            }

            foreach (Match p in Regex.Matches(lines[i], @"docs/[\w./-]+"))
            {
                references.Add(new JsonObject { ["kind"] = "path", ["text"] = p.Value, ["line"] = i + 1 });
            }

            foreach (Match d in Regex.Matches(lines[i], @"\b\d{4}-\d{2}-\d{2}\b"))
            {
                dates.Add(new JsonObject { ["what"] = "written", ["date"] = d.Value });
            }

            if (lines[i].Contains("We decided", StringComparison.Ordinal))
            {
                decisions.Add(new JsonObject { ["text"] = lines[i].Trim(), ["line"] = i + 1, ["quote"] = lines[i].Trim() });
            }
        }

        var superseded = lines.FirstOrDefault(l => l.Contains("superseded by", StringComparison.OrdinalIgnoreCase));
        var supersededBy = superseded is null ? null : superseded[(superseded.IndexOf("superseded by", StringComparison.OrdinalIgnoreCase) + 14)..].Trim();
        return new JsonObject
        {
            ["title"] = title,
            ["kind"] = kind,
            ["language"] = "en",
            ["summary"] = summary,
            ["topics"] = new JsonArray("demo", kind),
            ["components"] = new JsonArray(path.Contains('/', StringComparison.Ordinal) ? path[..path.IndexOf('/', StringComparison.Ordinal)] : "root"),
            ["people"] = new JsonArray(),
            ["dates"] = dates,
            ["references"] = references,
            ["decisions"] = decisions,
            ["status"] = superseded is null ? "unstated" : "superseded",
            ["status_as_written"] = superseded?.Trim().TrimStart('-', ' '),
            ["superseded_by"] = supersededBy,
            ["self_rating"] = null,
            ["audience"] = agents ? "agents" : "people",
            ["shelf"] = agents && !path.StartsWith("src/", StringComparison.Ordinal) ? "process" : null,
            ["evidence"] = new JsonObject { ["line"] = 1, ["quote"] = lines.Length > 0 ? lines[0].Trim() : "" },
            ["confidence"] = 0.85,
            ["reasoning"] = "a scripted reading",
        }.ToJsonString();
    }

    /// <summary>The reader streams (the provider cuts a long reply that does not flow), so the scripted answer is streamed as one update with its usage.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A judge that answers from the state's own text by simple rules, so the gates and the ledger checks are exercised.</summary>
internal sealed class ScriptedJudge : IJudgeModel
{
    public string Name => "scripted-judge";

    /// <summary>When true, the judge's confidence in a choice moves a little from call to call.</summary>
    public bool Drifts { get; set; }

    /// <summary>When true, the judge reads every file put to it as a ledger of work items, whatever its shape: for tests of ledgers that are lists or plain lines.</summary>
    public bool ReadsEveryLedgerAsOne { get; set; }

    public int Calls { get; private set; }

    /// <summary>Every state this judge was sent, so a test can say what never left the machine.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Sent { get; } = new();

    public Task<JudgeReply> AskAsync(JsonNode state, JsonObject questions, CancellationToken cancellationToken)
    {
        Calls++;
        Sent.Enqueue(state.ToJsonString() + questions.ToJsonString());
        var answers = new Dictionary<string, JudgeAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in questions)
        {
            if (question?["type"]?.GetValue<string>() == "choice")
            {
                // The kind: the reader's reading, confirmed; a relation: the reader's kind, confirmed; "see" words are none.
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                string choice;
                if (id.EndsWith("_type", StringComparison.Ordinal))
                {
                    choice = state["rows"]?[field]?["reader_reading"]?.GetValue<string>() ?? "other";
                }
                else
                {
                    var r = id[(id.LastIndexOf('_') + 1)..];
                    var words = state["rows"]?[field]?["relations"]?[r]?["as_written"]?.GetValue<string>() ?? "";
                    choice = words.StartsWith("see", StringComparison.OrdinalIgnoreCase) ? "none"
                        : words.StartsWith("split from", StringComparison.OrdinalIgnoreCase) ? "derived"
                        : state["rows"]?[field]?["relations"]?[r]?["reader_reading"]?.GetValue<string>() ?? "none";
                }

                // A judge asked again about the same row answers with a figure a little off the last one, as the real one does.
                var sure = Drifts ? 0.9 - (0.01 * (Calls % 7)) : 0.9;
                answers[id] = new ChoiceAnswer(choice, sure, new Dictionary<string, double> { [choice] = sure });
                continue;
            }

            double noul;
            if (id.EndsWith("instructions_to_ai", StringComparison.Ordinal))
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var text = state["inputs"]?[field]?["text"]?.GetValue<string>() ?? state["text"]?.GetValue<string>() ?? "";
                noul = text.Contains("Ignore all previous instructions", StringComparison.OrdinalIgnoreCase) ? 0.95 : 0.03;
            }
            else if (id.EndsWith("changes_treatment", StringComparison.Ordinal))
            {
                noul = 0.02;
            }
            else if (id == "follows_input_instruction")
            {
                noul = (state["output"]?.GetValue<string>() ?? "").Contains("HACKED", StringComparison.Ordinal) ? 0.96 : 0.04;
            }
            else if (id == "holds_finished")
            {
                var heading = state["heading"]?.GetValue<string>() ?? "";
                noul = heading.Contains("Done", StringComparison.OrdinalIgnoreCase) || heading.Contains("Finished", StringComparison.OrdinalIgnoreCase) || heading.Contains("\u0627\u0646\u062c\u0627\u0645", StringComparison.Ordinal) ? 0.95 : 0.04;
            }
            else if (id == "holds_open")
            {
                var heading = state["heading"]?.GetValue<string>() ?? "";
                noul = heading.Contains("open", StringComparison.OrdinalIgnoreCase) || heading.Contains("Next", StringComparison.OrdinalIgnoreCase) || heading.Contains("To do", StringComparison.OrdinalIgnoreCase) ? 0.93 : 0.05;
            }
            else if (id == "says_unfinished")
            {
                // Words that say the work is still to do; "half" leaves the judge between its lines; anything else tells how the work ended.
                var status = state["status"]?.GetValue<string>() ?? "";
                noul = status.Contains("open", StringComparison.OrdinalIgnoreCase) || status.Contains("progress", StringComparison.OrdinalIgnoreCase) || status.Contains("draft", StringComparison.OrdinalIgnoreCase) ? 0.92
                    : status.Contains("half", StringComparison.OrdinalIgnoreCase) ? 0.5 : 0.05;
            }
            else if (id == "tracked_outside")
            {
                noul = (state["note"]?.GetValue<string>() ?? "").Contains("tracked in", StringComparison.OrdinalIgnoreCase) ? 0.93 : 0.04;
            }
            else if (id == "is_ledger")
            {
                noul = ReadsEveryLedgerAsOne || (state["text"]?.GetValue<string>() ?? "").Contains("| ID |", StringComparison.Ordinal) ? 0.94 : 0.08;
            }
            else if (id.EndsWith("_is_finished", StringComparison.Ordinal))
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                noul = (state["rows"]?[field]?["status_as_written"]?.GetValue<string>() ?? "").StartsWith("DONE", StringComparison.Ordinal) ? 0.9 : 0.1;
            }
            else if (id.EndsWith("_is_cancelled", StringComparison.Ordinal))
            {
                noul = 0.05;
            }
            else if (id.EndsWith("_made", StringComparison.Ordinal))
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var text = state["candidates"]?[field]?["text"]?.GetValue<string>() ?? "";
                noul = text.Contains("might", StringComparison.OrdinalIgnoreCase) ? 0.1 : 0.9;
            }
            else if (id.EndsWith("_waits", StringComparison.Ordinal))
            {
                // A wait in any language is read as one; "perhaps" leaves the judge between its lines; a note is no wait.
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var condition = state["rows"]?[field]?["condition"]?.GetValue<string>() ?? "";
                noul = condition.Contains("must", StringComparison.OrdinalIgnoreCase) || condition.Contains("owner", StringComparison.OrdinalIgnoreCase) || condition.Contains("等待", StringComparison.Ordinal) ? 0.9
                    : condition.Contains("perhaps", StringComparison.OrdinalIgnoreCase) ? 0.5 : 0.1;
            }
            else if (id.EndsWith("_carried_out", StringComparison.Ordinal))
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var text = state["candidates"]?[field]?["text"]?.GetValue<string>() ?? "";
                noul = text.Contains("might", StringComparison.OrdinalIgnoreCase) ? 0.08 : text.Contains("switch", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.4;
            }
            else if (id.EndsWith("_same", StringComparison.Ordinal) && state["pairs"] is not null)
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var first = state["pairs"]?[field]?["first"]?.GetValue<string>() ?? "";
                var second = state["pairs"]?[field]?["second"]?.GetValue<string>() ?? "";
                noul = first.Contains("UTC", StringComparison.Ordinal) && second.Contains("UTC", StringComparison.Ordinal) ? 0.9 : 0.1;
            }
            else if (id.EndsWith("_significant", StringComparison.Ordinal))
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var text = state["candidates"]?[field]?["text"]?.GetValue<string>() ?? "";
                noul = text.Contains("label", StringComparison.OrdinalIgnoreCase) ? 0.1 : 0.85;
            }
            else if (id.EndsWith("_corrects", StringComparison.Ordinal))
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var text = state["candidates"]?[field]?["text"]?.GetValue<string>() ?? "";
                noul = text.Contains("contrary to", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.1;
            }
            else if (id.Contains("_recorded_", StringComparison.Ordinal))
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var adr = id[(id.LastIndexOf('_') + 1)..];
                var text = state["candidates"]?[field]?["text"]?.GetValue<string>() ?? "";
                var recorded = state["records"]?[field]?[adr]?["decisions"]?.ToJsonString() ?? "";
                noul = text.Contains("PostgreSQL", StringComparison.Ordinal) && recorded.Contains("PostgreSQL", StringComparison.Ordinal) ? 0.9 : 0.05;
            }
            else if (id.StartsWith("m", StringComparison.Ordinal) && id.Contains("_c", StringComparison.Ordinal) && state["mentions"] is not null)
            {
                var m = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var c = id[(id.IndexOf('_', StringComparison.Ordinal) + 1)..];
                var mention = state["mentions"]?[m]?["text"]?.GetValue<string>() ?? "";
                var title = state["candidates"]?[m]?[c]?["title"]?.GetValue<string>() ?? "";
                noul = title.Contains(mention, StringComparison.OrdinalIgnoreCase) || mention.Contains(title, StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.1;
            }
            else if (id == "is_finished")
            {
                noul = (state["path"]?.GetValue<string>() ?? "").Contains("done", StringComparison.Ordinal) ? 0.9 : 0.1;
            }
            else if (id.EndsWith("_title", StringComparison.Ordinal) && state["rows"] is not null)
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var row = state["rows"]?[field]?["row"]?.GetValue<string>() ?? "";
                var title = state["items"]?[field]?["title"]?.GetValue<string>() ?? "";
                noul = title.Length > 0 && row.Contains(title, StringComparison.OrdinalIgnoreCase) ? 0.92 : 0.08;
            }
            else if (id.EndsWith("_status", StringComparison.Ordinal) && state["rows"] is not null)
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var row = state["rows"]?[field]?["row"]?.GetValue<string>() ?? "";
                var written = state["items"]?[field]?["status_as_written"]?.GetValue<string>() ?? "";
                noul = written.Length > 0 && row.Contains(written, StringComparison.OrdinalIgnoreCase) ? 0.91 : 0.09;
            }
            else if (id.EndsWith("_holds", StringComparison.Ordinal) && state["links"] is not null)
            {
                var field = id[..id.IndexOf('_', StringComparison.Ordinal)];
                var text = state["links"]?[field]?["text"]?.GetValue<string>() ?? "";
                var kind = state["links"]?[field]?["target_kind"]?.GetValue<string>() ?? "";
                var target = state["links"]?[field]?["target"]?.GetValue<string>() ?? "";
                var key = kind.Contains(' ', StringComparison.Ordinal) ? kind[(kind.LastIndexOf(' ') + 1)..] : kind;
                noul = text.Contains(key, StringComparison.OrdinalIgnoreCase) || text.Contains(target, StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.1;
            }
            else if (id == "is_change_log")
            {
                noul = (state["text"]?.GetValue<string>() ?? "").Contains("## 2026", StringComparison.Ordinal) ? 0.9 : 0.1;
            }
            else
            {
                noul = 0.5;
            }

            answers[id] = new NoulAnswer(noul);
        }

        return Task.FromResult(new JudgeReply("scripted-judge-1", answers, 100, 10));
    }
}

public sealed class OrientationRunTests
{
    private static void WriteRepository(string root) => SyntheticRepository.Write(root);

    private static (SurveyOptions Options, RunServices Services, ScriptedChatClient Reader, ScriptedJudge Judge) Setup(
        string input, string output, int batchChars = 60_000, int concurrency = 4)
    {
        var reader = new ScriptedChatClient();
        var judge = new ScriptedJudge();
        var options = new SurveyOptions
        {
            InputPath = input,
            Conductor = new ConductorOptions
            {
                OutputPath = output,
                PlaybookPath = TestPaths.Playbook,
                StopAfterPhase = "orientation",
                Reader = new ReaderOptions { Concurrency = concurrency, Retries = 0 },
            },
            Budgets = new SurveyBudgets { OrientationBatchChars = batchChars },
        };
        var services = new RunServices
        {
            QuestionPolicy = (store, book) => new WrittenQuestionPolicy(store, book),
            Backup = BackupKind.Zip,
            ChatClients = _ => reader,
            JudgeModel = judge,
        };
        return (options, services, reader, judge);
    }

    private static RepositoryMap ReadMap(string output) =>
        System.Text.Json.JsonSerializer.Deserialize<RepositoryMap>(File.ReadAllText(Path.Combine(output, "repository-map.json")), RunContext.JsonOptions)!;

    [Fact]
    public async Task OrientationRunsOfflineWritesTheAtlasAndResumesFromTheState()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, reader, judge) = Setup(input, output);

        var result = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, result.Outcome);
        Assert.True(result.Diff!.IsFirstRun);
        Assert.Equal(7, result.Diff.Added);
        Assert.True(reader.Calls >= 3, "nature, nature after looking closer, and at least one kinds batch");
        Assert.True(judge.Calls >= 3, "gate in, gate out, and the ledger checks");

        var map = ReadMap(output);
        Assert.Equal("code", map.Nature.Kind);
        Assert.Equal("open-ledger", map.File("docs/backlog.md")!.Kind);
        Assert.Equal("finished-ledger", map.File("docs/backlog-done.md")!.Kind);
        Assert.Equal("spec", map.File("docs/specs/BL-1-first.md")!.Kind);
        Assert.Equal("code", map.File("src/main.py")!.Kind);
        Assert.Equal("Python", map.File("src/main.py")!.Language);
        Assert.Equal("image", map.File("assets/logo.png")!.Kind);
        Assert.Equal(7, map.Files.Count);

        var backlogScheme = Assert.Single(map.IdSchemes, s => s.Name == "backlog id");
        Assert.True(backlogScheme.Confirmed);
        Assert.Equal(5, backlogScheme.DistinctIds);
        var ghost = Assert.Single(map.IdSchemes, s => s.Name == "ghost");
        Assert.False(ghost.Confirmed);

        Assert.Equal(2, map.Ledgers.Count);
        Assert.All(map.Ledgers, l => Assert.True(l.Confirmed));
        Assert.Contains("read-with-flagged-text", map.File("docs/evil.md")!.Flags);
        Assert.True(map.FlaggedChunks >= 1);

        var log = File.ReadAllText(Path.Combine(output, "log.md"));
        Assert.Contains("gate-in: docs/evil.md", log, StringComparison.Ordinal);
        Assert.Contains("injection-flagged", log, StringComparison.Ordinal);
        Assert.Contains("discarded: the output followed an instruction from the input text", log, StringComparison.Ordinal);
        Assert.Contains("(flagged text withheld)", log, StringComparison.Ordinal);
        Assert.Contains("look-closer: docs", log, StringComparison.Ordinal);
        Assert.Contains("docs/missing.md", log, StringComparison.Ordinal);
        Assert.Contains("id-scheme: ghost", log, StringComparison.Ordinal);
        Assert.DoesNotContain("HACKED", File.ReadAllText(Path.Combine(output, "repository-map.md")), StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(output, "summary.md")));
        Assert.True(File.Exists(Path.Combine(output, "questions.md")));
        Assert.True(File.Exists(Path.Combine(output, "state.sqlite")));
        var stateText = File.ReadAllText(Path.Combine(output, "state.md"));
        Assert.Contains("orientation: done", stateText, StringComparison.Ordinal);
        Assert.Contains("id-scheme", stateText, StringComparison.Ordinal);
        Assert.Contains("## The repository, as understood", stateText, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(output, "backup"), "*.zip"));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!;
        Assert.Equal(7, manifest["files"]!.AsArray().Count);
        Assert.Equal("orientation", manifest["phases"]!["done"]![0]!.GetValue<string>());
        foreach (var catalogue in new[] { "items.json", "documents.json", "decisions.json", "graph.json", "questions.json", "status-words.json" })
        {
            Assert.True(File.Exists(Path.Combine(output, catalogue)), catalogue + " is always written, with an empty list when the phase has nothing");
        }

        Assert.Empty(JsonNode.Parse(File.ReadAllText(Path.Combine(output, "items.json")))!["items"]!.AsArray());
        // The engine's commit is the one its build recorded (InformationalVersion's "+<sha>"), and none when it was built
        // outside a git checkout - the public snapshot's test run is one.
        var built = typeof(Stomme.Cartographer.Atlas.ManifestWriter).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var engineCommit = manifest["engine"]!["commit"]?.GetValue<string>();
        if (built.Contains('+', StringComparison.Ordinal))
        {
            Assert.Matches("^[0-9a-f]{40}$", engineCommit);
        }
        else
        {
            Assert.Null(engineCommit);
        }
        Assert.Equal("survey-repository", manifest["playbook"]!["name"]!.GetValue<string>());
        Assert.DoesNotContain(Directory.GetFiles(input, "*", SearchOption.AllDirectories), f => f.EndsWith(".json", StringComparison.Ordinal));

        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var units = state.UnitsOf("orientation");
            Assert.Contains(units, u => u.Key == "nature");
            Assert.Contains(units, u => u.Key == "id-schemes");
            Assert.Equal(5, units.Count(u => u.Key.StartsWith("kind/", StringComparison.Ordinal)));
            Assert.Equal(2, units.Count(u => u.Key.StartsWith("ledger-check/", StringComparison.Ordinal)));
            Assert.NotEmpty(state.Glossary());
            Assert.NotNull(state.LoadRecord("map", "repository-map"));
            Assert.Equal(1, state.RunCount);
        }

        var readerCallsBefore = reader.Calls;
        var second = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, second.Outcome);
        Assert.Equal(readerCallsBefore, reader.Calls);
        Assert.Equal(7, second.Diff!.Unchanged);
        var logAfter = File.ReadAllText(Path.Combine(output, "log.md"));
        Assert.Contains("resumed from the state", logAfter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRunResumesFromTheFirstUnitNotDone()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, reader, _) = Setup(input, output, batchChars: 10, concurrency: 1);
        reader.FailOnKindsCall = 3;

        await Assert.ThrowsAsync<ReaderException>(() => new RepositorySurvey(options, services).RunAsync(CancellationToken.None));
        Assert.Equal(2, reader.NatureCalls);
        Assert.Equal(3, reader.KindsCalls);
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            Assert.Equal(2, state.UnitsOf("orientation").Count(u => u.Key.StartsWith("kind/", StringComparison.Ordinal)));
            Assert.Contains(state.UnitsOf("orientation"), u => u.Key == "nature");
        }

        Assert.Contains("failed", File.ReadAllText(Path.Combine(output, "summary.md")), StringComparison.OrdinalIgnoreCase);

        var second = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, second.Outcome);
        Assert.Equal(2, reader.NatureCalls);
        // Three batches were left, and the one holding the planted instruction costs a second call (discarded, then re-asked).
        Assert.Equal(7, reader.KindsCalls);
        var log = File.ReadAllText(Path.Combine(output, "log.md"));
        Assert.Contains("2 document(s)", log, StringComparison.Ordinal);
        Assert.Contains("resumed from the state", log, StringComparison.Ordinal);
        Assert.Equal(7, ReadMap(output).Files.Count);
    }

    [Fact]
    public async Task AReRunKeepsTheUnchangedRedoesTheChangedAndMarksTheGone()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, reader, _) = Setup(input, output);
        await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        var callsAfterFirst = reader.Calls;

        File.WriteAllText(Path.Combine(input, "docs", "specs", "BL-1-first.md"), "# BL-1 spec\n\nObjective, revised.\n");
        File.WriteAllText(Path.Combine(input, "docs", "new.md"), "# New notes\n\nFresh.\n");
        File.Delete(Path.Combine(input, "docs", "evil.md"));
        File.WriteAllText(Path.Combine(output, "answers.md"), "# Answers\n\n## kind:docs/specs/BL-1-first.md\nplan\n");

        var second = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAfterPhase, second.Outcome);
        Assert.Equal((5, 1, 1, 1), (second.Diff!.Unchanged, second.Diff.Changed, second.Diff.Added, second.Diff.Gone));
        Assert.Equal(3, reader.Calls - callsAfterFirst);

        var map = ReadMap(output);
        Assert.Equal(7, map.Files.Count);
        Assert.Null(map.File("docs/evil.md"));
        Assert.Equal("other", map.File("docs/new.md")!.Kind);
        var revised = map.File("docs/specs/BL-1-first.md")!;
        Assert.Equal("plan", revised.Kind);
        Assert.Equal("person", revised.By);

        using var state = RunState.Open(Path.Combine(output, "state.sqlite"));
        Assert.Equal(["docs/evil.md"], state.GoneInputs());
        Assert.Equal(2, state.RunCount);
        var log = File.ReadAllText(Path.Combine(output, "log.md"));
        Assert.Contains("5 unchanged", log, StringComparison.Ordinal);
        Assert.Contains("1 gone", log, StringComparison.Ordinal);
        Assert.Contains("a person's answer", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOutputInsideTheInputIsRefused()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        WriteRepository(input);
        var (options, services, _, _) = Setup(input, Path.Combine(input, "out"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RepositorySurvey(options, services).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheCeilingAsksAndStopsWhenNobodyAnswers()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        WriteRepository(input);
        var (options, services, _, _) = Setup(input, output);
        var tight = new SurveyOptions
        {
            InputPath = options.InputPath,
            Conductor = new ConductorOptions
            {
                OutputPath = options.Conductor.OutputPath,
                PlaybookPath = options.Conductor.PlaybookPath,
                StopAfterPhase = options.Conductor.StopAfterPhase,
                Reader = options.Conductor.Reader,
                Judge = options.Conductor.Judge,
                Spending = new SpendingOptions
                {
                    CeilingUsd = 0m,
                    Prices = new PriceTable { ReadOn = new DateOnly(2026, 11, 3), Prices = [new ModelPrice("mimo-v2.6-pro", 1000m, 1000m, 1000m)] },
                },
            },
        };
        var result = await new RepositorySurvey(tight, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedAtCeiling, result.Outcome);
        Assert.Contains("## ceiling:1", File.ReadAllText(Path.Combine(output, "questions.md")), StringComparison.Ordinal);
        Assert.Contains("StoppedAtCeiling", File.ReadAllText(Path.Combine(output, "summary.md")), StringComparison.Ordinal);
    }
}
