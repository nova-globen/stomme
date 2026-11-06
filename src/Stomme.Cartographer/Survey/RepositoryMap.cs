namespace Stomme.Cartographer.Survey;

/// <summary>A file, a line and a quote, as the map and the bundle carry them.</summary>
public sealed class EvidenceEntry
{
    public string? Path { get; set; }
    public int? Line { get; set; }
    public string? Quote { get; set; }
}

/// <summary>What the repository is (ADR 0016): code, documentation, design, data, mixed, or a word the reader chose.</summary>
public sealed class NatureEntry
{
    public string Kind { get; set; } = "unknown";
    public string Description { get; set; } = "";
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = "";
    public List<EvidenceEntry> Evidence { get; set; } = [];
}

/// <summary>An id scheme the reader proposed and the engine confirmed or dropped by scanning (ADR 0015).</summary>
public sealed class IdScheme
{
    public string Name { get; set; } = "";
    public string Example { get; set; } = "";
    public string Regex { get; set; } = "";
    public int? NumericGroup { get; set; }
    public string RefersTo { get; set; } = "";
    public string? WhereDefined { get; set; }
    public string Reasoning { get; set; } = "";
    public bool Confirmed { get; set; }
    public int Matches { get; set; }
    public int DistinctIds { get; set; }
    public int MatchesInDocuments { get; set; }
    public string? Note { get; set; }
}

/// <summary>A status or tier word as the repository uses it, with its meaning and the engine's mapping.</summary>
public sealed class VocabularyEntry
{
    public string Word { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string? Mapped { get; set; }
}

public sealed class SpecIndexEntry
{
    public string Path { get; set; } = "";
    public string? Anchor { get; set; }
    public string Reasoning { get; set; } = "";
}

/// <summary>A file the guides or the reader named as a ledger of work items or a change log.</summary>
public sealed class LedgerEntry
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string? NamedBy { get; set; }
    public bool? Confirmed { get; set; }
    public Dictionary<string, double>? JudgeFigures { get; set; }
}

/// <summary>A ledger the guides name that is not in the input. A person is asked, and the verify phase counts it as a finding.</summary>
public sealed class MissingLedger
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Reasoning { get; set; } = "";

    /// <summary>Where a guide writes the path, as "path:line"; found by code, so a guess of the reader's is never here.</summary>
    public string? NamedIn { get; set; }

    /// <summary>True when a person answered that the guide is out of date: no finding.</summary>
    public bool Dismissed { get; set; }
}

/// <summary>A place outside the repository where it says its work is tracked: a tracker's address in a guide (by rule), or a note of the reader's that the judge read so.</summary>
public sealed class WorkElsewhere
{
    public string Where { get; set; } = "";
    public string? Path { get; set; }
    public int? Line { get; set; }
    public string NamedIn { get; set; } = "";
    public string By { get; set; } = "rule";
    public double? Probability { get; set; }

    /// <summary>True when a person answered that nothing kept there belongs with the import: no finding.</summary>
    public bool Dismissed { get; set; }
}

/// <summary>One file of the map: its category by rule, its kind by the reader or by rule, and why.</summary>
public sealed class MappedFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Title { get; set; }
    public string? Language { get; set; }
    public double? Confidence { get; set; }
    public int? EvidenceLine { get; set; }
    public string? EvidenceQuote { get; set; }
    public string? Reasoning { get; set; }
    public string By { get; set; } = "rule";
    public List<string> Flags { get; set; } = [];
    public List<string> Alternatives { get; set; } = [];
}

/// <summary>
/// The repository map (plan, phase 0): what the repository is, how it names things, where the work items and
/// the documents live, and what every file is. Written as repository-map.json and repository-map.md, and kept
/// as the orientation phase's checkpoint.
/// </summary>
public sealed class RepositoryMap
{
    public string MapVersion { get; set; } = "1";
    public NatureEntry Nature { get; set; } = new();
    public List<string> NaturalLanguages { get; set; } = [];
    public List<string> ProgrammingLanguages { get; set; } = [];
    public List<string> Guides { get; set; } = [];
    public List<IdScheme> IdSchemes { get; set; } = [];
    public List<VocabularyEntry> StatusVocabulary { get; set; } = [];
    public List<VocabularyEntry> TierVocabulary { get; set; } = [];
    public SpecIndexEntry? SpecIndex { get; set; }
    public List<LedgerEntry> Ledgers { get; set; } = [];

    /// <summary>Ledgers the guides name that are not in the input: their rows are not in the import.</summary>
    public List<MissingLedger> MissingLedgers { get; set; } = [];

    /// <summary>Places outside the repository where it says its work is tracked; nothing kept there is in the import.</summary>
    public List<WorkElsewhere> WorkElsewhere { get; set; } = [];
    public List<string> DocumentRoots { get; set; } = [];
    public bool DocumentsThin { get; set; }
    public string ThinReasoning { get; set; } = "";
    public string? ReaderThinView { get; set; }
    public List<MappedFile> Files { get; set; } = [];
    public Dictionary<string, int> CountsByKind { get; set; } = [];
    public Dictionary<string, int> CountsByCategory { get; set; } = [];
    public int FlaggedChunks { get; set; }
    public List<string> Notes { get; set; } = [];
    public string Reasoning { get; set; } = "";

    public MappedFile? File(string path) => Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));
}
