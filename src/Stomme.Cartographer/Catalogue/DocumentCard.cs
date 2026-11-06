namespace Stomme.Cartographer.Catalogue;

/// <summary>Where a document came from: the file as read, with its hash (ADR 0005, 0017).</summary>
public sealed class DocumentSource
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Format { get; set; } = "";

    /// <summary>How many chunks the file was read in (ADR 0014); 1 for a file read whole.</summary>
    public int Chunks { get; set; }

    public string RunId { get; set; } = "";
}

/// <summary>Something a document points at (ADR 0012): an id, a path, an anchor, a version, a URL, or a document or item it names by title.</summary>
public sealed class DocumentReference
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>The line in the source, 1-based, when the reader gave one.</summary>
    public int? Line { get; set; }

    /// <summary>The key of the item an id of the item scheme resolves to (resolved by code, ADR 0020 rule 2).</summary>
    public string? Item { get; set; }

    /// <summary>The source path of the document a path resolves to, when it is a document of the atlas.</summary>
    public string? Document { get; set; }
}

public sealed class DocumentDate
{
    public string What { get; set; } = "";
    public string Date { get; set; } = "";
}

/// <summary>A statement the reader took for a decision that was made (ADR 0006): raw material for the decisions phase, which has the judge check it.</summary>
public sealed class DocumentDecision
{
    public string Text { get; set; } = "";
    public int? Line { get; set; }
    public string? Quote { get; set; }
}

/// <summary>
/// One document of the catalogue (the plan, phase 2; ADR 0005's document): what it is, what it says, what it
/// names, and what it says about its own standing (ADR 0020 rule 9). Kept as a record in the state by its
/// source path; the atlas renders it with the source text as its body.
/// </summary>
public sealed class DocumentCard
{
    /// <summary>The source path, which is the document's identity.</summary>
    public string Path { get; set; } = "";

    public string? Title { get; set; }

    /// <summary>The kind the whole text shows (data/kinds.json); orientation's reading of the head is kept beside it.</summary>
    public string Kind { get; set; } = "other";

    public string? KindByOrientation { get; set; }

    public string? Language { get; set; }

    /// <summary>Two to five sentences in English; for a chunked file, the chunks' summaries joined by the engine.</summary>
    public string Summary { get; set; } = "";

    public List<string> Topics { get; set; } = [];

    public List<string> Components { get; set; } = [];

    public List<string> People { get; set; } = [];

    public List<DocumentDate> Dates { get; set; } = [];

    public List<DocumentReference> References { get; set; } = [];

    public List<DocumentDecision> Decisions { get; set; } = [];

    /// <summary>What the document says about itself (data/document-statuses.json): current, draft, accepted, superseded, deprecated, snapshot or unstated.</summary>
    public string Status { get; set; } = "unstated";

    public string? StatusAsWritten { get; set; }

    /// <summary>What the document says replaces it, as written.</summary>
    public string? SupersededBy { get; set; }

    /// <summary>How the document rates its own reliability or currency, as written.</summary>
    public string? SelfRating { get; set; }

    /// <summary>people, agents or both: who the text is written for.</summary>
    public string Audience { get; set; } = "people";

    /// <summary>For a text written for agents (ADR 0020 rule 8): process (how to work in this repository) or product (guidance the product ships).</summary>
    public string? Shelf { get; set; }

    /// <summary>True for a file written for AI agents or tools, by its path or by the reader's word (ADR 0020 rule 8).</summary>
    public bool ForAgents { get; set; }

    /// <summary>True when the atlas copy is reference-only: git stays the owner (ADR 0020 rule 8).</summary>
    public bool ReferenceOnly { get; set; }

    public DocumentSource Source { get; set; } = new();

    /// <summary>True when the file is gone from the input; the card is kept, never deleted (ADR 0017).</summary>
    public bool SourceGone { get; set; }

    public List<string> Flags { get; set; } = [];

    public int? EvidenceLine { get; set; }

    public string? EvidenceQuote { get; set; }

    public double? Confidence { get; set; }

    public string? Reasoning { get; set; }

    /// <summary>Who made the card: reader, or rule when the reader's output was discarded.</summary>
    public string By { get; set; } = "reader";
}
