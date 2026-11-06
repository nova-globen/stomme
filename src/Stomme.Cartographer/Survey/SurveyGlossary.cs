using Stomme.Conductor.State;

namespace Stomme.Cartographer.Survey;

/// <summary>What the survey puts into the engine's glossary (ADR 0017): the map's facts, by the kinds the playbook orders.</summary>
public static class SurveyGlossary
{
    /// <summary>What orientation learned, as glossary entries.</summary>
    public static void AddFromMap(this Glossary glossary, RepositoryMap map)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        ArgumentNullException.ThrowIfNull(map);
        glossary.Add("repository", "nature", $"{map.Nature.Kind}: {map.Nature.Description}", "orientation");
        if (map.NaturalLanguages.Count > 0)
        {
            glossary.Add("repository", "languages", string.Join(", ", map.NaturalLanguages), "orientation");
        }

        foreach (var scheme in map.IdSchemes.Where(s => s.Confirmed))
        {
            glossary.Add("id-scheme", scheme.Name, $"like {scheme.Example}; refers to {scheme.RefersTo}; {scheme.DistinctIds} distinct ids seen{(scheme.WhereDefined is null ? "" : "; defined in " + scheme.WhereDefined)}", scheme.WhereDefined);
        }

        foreach (var word in map.StatusVocabulary)
        {
            glossary.Add("status", word.Word, word.Meaning + (word.Mapped is null ? "" : $" (mapped to {word.Mapped})"), "orientation");
        }

        foreach (var word in map.TierVocabulary)
        {
            glossary.Add("tier", word.Word, word.Meaning, "orientation");
        }

        foreach (var ledger in map.Ledgers)
        {
            glossary.Add("ledger", ledger.Path, ledger.Kind + (ledger.Confirmed is true ? ", confirmed by the judge" : ""), ledger.Path);
        }

        if (map.SpecIndex is not null)
        {
            glossary.Add("index", "specs", $"indexed at {map.SpecIndex.Path}{(map.SpecIndex.Anchor is null ? "" : "#" + map.SpecIndex.Anchor)}", map.SpecIndex.Path);
        }

        foreach (var note in map.Notes.Take(20))
        {
            glossary.Add("note", note.Length <= 60 ? note : note[..60] + "...", note, "orientation");
        }

        if (map.DocumentRoots.Count > 0)
        {
            glossary.Add("repository", "document roots", string.Join(", ", map.DocumentRoots), "orientation");
        }
    }
}
