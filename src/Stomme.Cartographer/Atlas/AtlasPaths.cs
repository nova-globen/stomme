namespace Stomme.Cartographer.Atlas;

/// <summary>Where the atlas goes (ADR 0005, 0018): the map, the summary, the manifest and the backup, and later the items, documents and decisions, beside the engine's own output.</summary>
public sealed class AtlasPaths
{
    public AtlasPaths(string root)
    {
        Root = root;
        Backup = Path.Combine(root, "backup");
        Items = Path.Combine(root, "items");
        Documents = Path.Combine(root, "documents");
        Decisions = Path.Combine(root, "decisions");
        Summary = Path.Combine(root, "summary.md");
        Manifest = Path.Combine(root, "manifest.json");
        MapJson = Path.Combine(root, "repository-map.json");
        MapMarkdown = Path.Combine(root, "repository-map.md");
    }

    public string Root { get; }
    public string Backup { get; }
    public string Items { get; }
    public string Documents { get; }
    public string Decisions { get; }
    public string Summary { get; }
    public string Manifest { get; }
    public string MapJson { get; }
    public string MapMarkdown { get; }
}
