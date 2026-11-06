namespace Stomme.Cartographer.Catalogue;

/// <summary>Where a work item came from: the ledger row that defines it, with the hash of the file as read (ADR 0017).</summary>
public sealed class ItemSource
{
    public string Path { get; set; } = "";
    public int? Line { get; set; }
    public string Sha256 { get; set; } = "";
    public int ChunkIndex { get; set; }
    public string LedgerKind { get; set; } = "";
    public string RunId { get; set; } = "";
}

/// <summary>Something a row points at: an id, a path, an anchor, a version or a URL (ADR 0012, resolved by the links phase; ids of the item scheme are resolved here).</summary>
public sealed class ItemReference
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>The key of the item this reference resolves to, when it is an id of the item scheme that a row or a placeholder defines.</summary>
    public string? ResolvedKey { get; set; }
}

/// <summary>A link the row states to another work item: its kind (parent, child, blocker, blocks, related, duplicate, supersedes), the other's id as written and resolved, the words that state it, who read it and how surely.</summary>
public sealed class ItemRelation
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string? AsWritten { get; set; }
    public string? ResolvedKey { get; set; }
    public string By { get; set; } = "reader";
    public double? Confidence { get; set; }
    public List<string> Flags { get; set; } = [];
}

public sealed class ItemDate
{
    public string What { get; set; } = "";
    public string Date { get; set; } = "";
}

/// <summary>
/// One work item of the catalogue (the plan, phase 1; ADR 0005's item): its number, its source ids, its status
/// as written and as mapped with who mapped it, and its provenance. Kept as a record in the state, rendered
/// into the atlas.
/// </summary>
public sealed class WorkItem
{
    /// <summary>The item's number (ADR 0015): the scheme's number where the scheme is numeric, else assigned in source order.</summary>
    public int Number { get; set; }

    /// <summary>The normalised id (ADR 0020 rule 1), or the assigned number as text when the ledger gives no id.</summary>
    public string Key { get; set; } = "";

    /// <summary>Every id the row carries for itself, as written.</summary>
    public List<string> Aliases { get; set; } = [];

    public string Title { get; set; } = "";

    public string? StatusAsWritten { get; set; }

    /// <summary>The mapped state (data/statuses.json), or unknown for a placeholder.</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Who mapped the status: rule, reader, judge or person.</summary>
    public string StatusBy { get; set; } = "rule";

    /// <summary>What the item waits on when it is deferred (ADR 0020 rule 12).</summary>
    public string? Condition { get; set; }

    /// <summary>The kind of work the row's own words say (bug, feature, task, spike, chore, question, epic, decision, documentation), or null; with the words, who decided and how surely.</summary>
    public string? Type { get; set; }
    public string? TypeAsWritten { get; set; }
    public string? TypeBy { get; set; }
    public double? TypeConfidence { get; set; }

    /// <summary>How surely the status was read: the reader's confidence on the row, or the judge's figure when it decided.</summary>
    public double? StatusConfidence { get; set; }

    /// <summary>How surely the row's condition says the item waits on someone or something (the judge, a person, or the word list as a fallback), and who decided; null when the row has no condition.</summary>
    public double? Waits { get; set; }
    public string? WaitsBy { get; set; }

    /// <summary>The links to other items the row states, with their kind, who read them and how surely; parent and blockers are read from here.</summary>
    public List<ItemRelation> Relations { get; set; } = [];

    public string? Parent => Relations.FirstOrDefault(r => r.Kind == "parent" && r.ResolvedKey is not null)?.ResolvedKey;

    public IEnumerable<string> Blockers => Relations.Where(r => r.Kind == "blocker" && r.ResolvedKey is not null).Select(r => r.ResolvedKey!);

    /// <summary>The heading of the section the row sits under in its ledger, as written; null when it sits under none. It tells two rows with the same title apart.</summary>
    public string? Section { get; set; }

    /// <summary>SHA-256 over the row's raw lines in its file, by code; null for a row that was not found in the file as written and for a placeholder.</summary>
    public string? RowSha256 { get; set; }

    /// <summary>The row's raw lines as its ledger wrote them when it was read (kept in the state, never written to the bundle): by them code says whether the row is still in its ledger.</summary>
    public string? RowRaw { get; set; }

    public string? Tier { get; set; }

    public string? Size { get; set; }

    public List<ItemDate> Dates { get; set; } = [];

    /// <summary>The row's text beyond its title, as markdown.</summary>
    public string Detail { get; set; } = "";

    /// <summary>The reader's account of the row, kept when <see cref="Detail"/> is the row's own text by code.</summary>
    public string? Summary { get; set; }

    public List<ItemReference> References { get; set; } = [];

    public List<ItemSource> Sources { get; set; } = [];

    /// <summary>True for an id the rows refer to that nothing defines (ADR 0015).</summary>
    public bool Placeholder { get; set; }

    /// <summary>True when the row or the ledger it came from is gone from the input; the item is kept, never deleted (ADR 0017).</summary>
    public bool SourceGone { get; set; }

    public List<string> Flags { get; set; } = [];

    public int? EvidenceLine { get; set; }

    public string? EvidenceQuote { get; set; }

    public double? Confidence { get; set; }

    public string? Reasoning { get; set; }

    /// <summary>Who made the item: reader (from a row), rule (a placeholder), person (from an answer).</summary>
    public string By { get; set; } = "reader";
}

/// <summary>Work listed in prose, without rows (ADR 0020 rule 11): a question for a person, kept with its answer.</summary>
public sealed class ProseWork
{
    public string Path { get; set; } = "";
    public string Heading { get; set; } = "";
    public List<string> Bullets { get; set; } = [];
    public string? Reasoning { get; set; }
    public string QuestionKey { get; set; } = "";

    /// <summary>The person's answer: items, notes or ignore; null while open.</summary>
    public string? Answer { get; set; }
}
