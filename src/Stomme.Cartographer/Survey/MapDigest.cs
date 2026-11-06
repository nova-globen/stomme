using System.Globalization;
using System.Text;
using Stomme.Cartographer.Sources;

namespace Stomme.Cartographer.Survey;

/// <summary>
/// The digest of the map that goes into the cached prefix of every prompt after orientation (ADR 0019): the
/// nature, the folders, the kinds by count, the id schemes, the vocabulary, the ledgers and the spec index, in
/// a bounded block. The map itself stays in the state; a unit retrieves the rows it needs.
/// </summary>
public static class MapDigest
{
    public static string Build(RepositoryMap map, RepositorySnapshot snapshot, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Nature: {map.Nature.Kind}. {map.Nature.Description}");
        if (map.NaturalLanguages.Count > 0 || map.ProgrammingLanguages.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Languages: {string.Join(", ", map.NaturalLanguages)}{(map.ProgrammingLanguages.Count == 0 ? "" : "; code in " + string.Join(", ", map.ProgrammingLanguages))}.");
        }

        var folders = snapshot.Files
            .GroupBy(f => f.Path.Contains('/', StringComparison.Ordinal) ? f.Path[..f.Path.IndexOf('/', StringComparison.Ordinal)] + "/" : "(root)")
            .OrderByDescending(g => g.Count())
            .Take(15)
            .Select(g => $"{g.Key} {g.Count()}");
        text.AppendLine("Folders by number of files: " + string.Join(", ", folders) + ".");

        var documentKinds = map.CountsByKind
            .Where(c => c.Key is not ("code" or "data" or "config" or "image" or "binary" or "design-file" or "archive"))
            .OrderByDescending(c => c.Value)
            .Take(15)
            .Select(c => $"{c.Key} {c.Value}");
        text.AppendLine("Documents by kind: " + string.Join(", ", documentKinds) + ".");

        var schemes = map.IdSchemes.Where(s => s.Confirmed).ToList();
        if (schemes.Count > 0)
        {
            text.AppendLine("Id schemes (confirmed by scanning):");
            foreach (var scheme in schemes)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {scheme.Name}: like {scheme.Example}, refers to {scheme.RefersTo}{(scheme.WhereDefined is null ? "" : ", defined in " + scheme.WhereDefined)}; {scheme.DistinctIds} distinct ids seen");
            }
        }

        if (map.StatusVocabulary.Count > 0)
        {
            text.AppendLine("Status words:");
            foreach (var word in map.StatusVocabulary)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {word.Word}: {word.Meaning}{(word.Mapped is null ? "" : " (mapped to " + word.Mapped + ")")}");
            }
        }

        if (map.TierVocabulary.Count > 0)
        {
            text.AppendLine("Tiers: " + string.Join("; ", map.TierVocabulary.Select(t => $"{t.Word}: {t.Meaning}")) + ".");
        }

        if (map.Ledgers.Count > 0)
        {
            text.AppendLine("Ledgers: " + string.Join("; ", map.Ledgers.Select(l => $"{l.Path} ({l.Kind}{(l.Confirmed is true ? ", confirmed by the judge" : "")})")) + ".");
        }

        if (map.SpecIndex is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Specs are indexed at {map.SpecIndex.Path}{(map.SpecIndex.Anchor is null ? "" : "#" + map.SpecIndex.Anchor)}.");
        }

        if (map.DocumentRoots.Count > 0)
        {
            text.AppendLine("Document roots: " + string.Join(", ", map.DocumentRoots) + ".");
        }

        var result = text.ToString().TrimEnd();
        return result.Length <= maxChars ? result : result[..Math.Max(0, maxChars - 22)] + "\n[cut by the engine]";
    }
}
