namespace Stomme.Cartographer.Catalogue;

/// <summary>Where a decision was found (ADR 0006, 0012): a line of a document, or a commit of the repository or of its older history (ADR 0021).</summary>
public sealed class DecisionSource
{
    /// <summary>document, commit or history-commit.</summary>
    public string Kind { get; set; } = "document";

    /// <summary>The document's path, or the commit's hash.</summary>
    public string Path { get; set; } = "";

    public int? Line { get; set; }

    public string? Quote { get; set; }

    /// <summary>The document's title, or the commit's subject.</summary>
    public string? Title { get; set; }

    /// <summary>YYYY-MM-DD when the source shows one.</summary>
    public string? Date { get; set; }

    /// <summary>The document's kind, when the source is a document.</summary>
    public string? DocumentKind { get; set; }
}

/// <summary>What the reader drafted from the evidence (ADR 0006).</summary>
public sealed class DecisionDraft
{
    public string Title { get; set; } = "";

    public string Context { get; set; } = "";

    public string Decision { get; set; } = "";

    public string Consequences { get; set; } = "";

    public string? Date { get; set; }

    public double? Confidence { get; set; }

    public string? Reasoning { get; set; }
}

/// <summary>
/// One decision found in the repository, merged from every place it was noted, with the judge's reading of it and its
/// draft when it gets one (ADR 0006). Its key is the hash of its words, so a re-run finds it again.
/// </summary>
public sealed class DecisionCard
{
    public string Key { get; set; } = "";

    /// <summary>The draft's number, when the decision has a file in decisions/ (ADR 0015 numbering, kept across runs).</summary>
    public int? Number { get; set; }

    /// <summary>The decision as noted, from the leading source.</summary>
    public string Text { get; set; } = "";

    public string? Title { get; set; }

    public List<DecisionSource> Sources { get; set; } = [];

    /// <summary>The earliest date the sources show, YYYY-MM-DD.</summary>
    public string? Date { get; set; }

    /// <summary>draft (a file in decisions/), record (kept for the graph only), recorded (already an ADR of the repository), not-a-decision, unsure.</summary>
    public string Standing { get; set; } = "candidate";

    public double? Made { get; set; }

    /// <summary>The judge's second reading of "made" (backlog TR-15): the text tells of a change carried out.</summary>
    public double? CarriedOut { get; set; }

    public double? Significant { get; set; }

    /// <summary>The path of the ADR document that already records it.</summary>
    public string? RecordedIn { get; set; }

    public double? Recorded { get; set; }

    public DecisionDraft? Draft { get; set; }

    /// <summary>Keys of the work items the text names.</summary>
    public List<string> Items { get; set; } = [];

    public List<string> Flags { get; set; } = [];

    public string By { get; set; } = "rule";

    public string RunId { get; set; } = "";
}
