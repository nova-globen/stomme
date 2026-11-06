using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Stomme.Conductor.Playbooks;

/// <summary>
/// A playbook (ADR 0018): the phases, the phase prompts, the JSON shapes the reader answers in, the judge's
/// questions and the data tables, kept as text so that a change to a prompt or a table is a data change, never
/// a build. The manifest is playbook.json. A playbook is loaded from a folder, or from the texts a host carries
/// embedded in its package (ADR 0022); the hash is the same either way for the same contents.
/// </summary>
public sealed class Playbook
{
    private readonly Dictionary<string, string> _prompts;
    private readonly Dictionary<string, string> _schemas;
    private readonly Dictionary<string, JsonObject> _data;

    private Playbook(
        string root, string name, string version, string hash, IReadOnlyList<string> phases, IReadOnlyList<string> glossaryKinds,
        Dictionary<string, string> prompts, Dictionary<string, string> schemas, JsonObject judge, Dictionary<string, JsonObject> data)
    {
        Root = root;
        Name = name;
        Version = version;
        Hash = hash;
        Phases = phases;
        GlossaryKinds = glossaryKinds;
        _prompts = prompts;
        _schemas = schemas;
        Judge = judge;
        _data = data;
    }

    /// <summary>The folder the playbook was loaded from, or "embedded" when the host carried it.</summary>
    public string Root { get; }

    public string Name { get; }

    public string Version { get; }

    /// <summary>A hash over everything in the playbook, so the state and the log know which playbook ran.</summary>
    public string Hash { get; }

    public IReadOnlyList<string> Phases { get; }

    /// <summary>The glossary's kinds in the order they are kept and rendered (ADR 0017); a kind not named comes last.</summary>
    public IReadOnlyList<string> GlossaryKinds { get; }

    /// <summary>The judge's question sets by name (judge/questions.json).</summary>
    public JsonObject Judge { get; }

    public string Prompt(string name) =>
        _prompts.TryGetValue(name, out var text) ? text : throw new KeyNotFoundException($"The playbook has no prompt named '{name}'.");

    public string Schema(string name) =>
        _schemas.TryGetValue(name, out var text) ? text : throw new KeyNotFoundException($"The playbook has no schema named '{name}'.");

    public JsonObject Data(string name) =>
        _data.TryGetValue(name, out var node) ? node : throw new KeyNotFoundException($"The playbook has no data table named '{name}'.");

    public JsonObject JudgeSet(string name) =>
        Judge[name] as JsonObject ?? throw new KeyNotFoundException($"The playbook's judge questions have no set named '{name}'.");

    /// <summary>
    /// A hash over only the parts of the playbook a unit of work uses (ADR 0017), so that a change to one
    /// phase's prompt does not make every other phase's units redo themselves; names it cannot find are hashed as absent.
    /// </summary>
    public string Salt(IEnumerable<string> prompts, IEnumerable<string> schemas, IEnumerable<string> data, IEnumerable<string> judgeSets)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(judgeSets);
        var text = new StringBuilder();
        foreach (var name in prompts)
        {
            text.Append("prompt:").Append(name).Append('\n').Append(_prompts.GetValueOrDefault(name) ?? "(absent)").Append('\n');
        }

        foreach (var name in schemas)
        {
            text.Append("schema:").Append(name).Append('\n').Append(_schemas.GetValueOrDefault(name) ?? "(absent)").Append('\n');
        }

        foreach (var name in data)
        {
            text.Append("data:").Append(name).Append('\n').Append(_data.TryGetValue(name, out var table) ? table.ToJsonString() : "(absent)").Append('\n');
        }

        foreach (var name in judgeSets)
        {
            text.Append("judge:").Append(name).Append('\n').Append(Judge[name]?.ToJsonString() ?? "(absent)").Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>A prompt with its {{placeholders}} filled.</summary>
    public string Prompt(string name, IReadOnlyDictionary<string, string> values)
    {
        var text = Prompt(name);
        foreach (var (key, value) in values)
        {
            text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Loads a playbook from its folder.</summary>
    public static Playbook Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "playbook.json")))
        {
            throw new FileNotFoundException("The playbook folder has no playbook.json.", Path.Combine(root, "playbook.json"));
        }

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            entries[Path.GetRelativePath(root, entry).Replace('\\', '/')] = File.ReadAllBytes(entry);
        }

        return FromEntries(root, entries);
    }

    /// <summary>Loads a playbook from its texts by relative path (playbook.json among them), as a host embeds them (ADR 0022).</summary>
    public static Playbook FromEntries(string root, IReadOnlyDictionary<string, byte[]> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!entries.TryGetValue("playbook.json", out var manifestBytes))
        {
            throw new FileNotFoundException("The playbook has no playbook.json.");
        }

        string Text(string relative) =>
            entries.TryGetValue(relative.Replace('\\', '/'), out var bytes)
                ? Encoding.UTF8.GetString(bytes)
                : throw new FileNotFoundException($"The playbook names {relative}, which it does not carry.");

        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(manifestBytes)) as JsonObject
            ?? throw new InvalidDataException("playbook.json is not a JSON object.");
        var name = manifest["name"]?.GetValue<string>() ?? "playbook";
        var version = manifest["version"]?.GetValue<string>() ?? "0";
        var phases = (manifest["phases"] as JsonArray ?? []).Select(p => p!.GetValue<string>()).ToList();
        var glossaryKinds = (manifest["glossary_kinds"] as JsonArray ?? []).Select(k => k!.GetValue<string>()).ToList();

        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in manifest["prompts"] as JsonObject ?? [])
        {
            prompts[key] = Text(node!.GetValue<string>());
        }

        var schemas = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in manifest["schemas"] as JsonObject ?? [])
        {
            schemas[key] = Text(node!.GetValue<string>());
        }

        var judge = JsonNode.Parse(Text(manifest["judge"]?.GetValue<string>() ?? "judge/questions.json")) as JsonObject
            ?? throw new InvalidDataException("The judge questions are not a JSON object.");

        var data = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, node) in manifest["data"] as JsonObject ?? [])
        {
            var relative = node!.GetValue<string>();
            data[key] = JsonNode.Parse(Text(relative)) as JsonObject
                ?? throw new InvalidDataException($"The playbook's data table {relative} is not a JSON object.");
        }

        var hash = new StringBuilder();
        foreach (var (relative, bytes) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            hash.Append(relative).Append('\n');
            hash.Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
        }

        var playbookHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString())))[..16];
        return new Playbook(root, name, version, playbookHash, phases, glossaryKinds, prompts, schemas, judge, data);
    }
}
