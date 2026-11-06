using Stomme.Conductor.Options;

namespace Stomme.Cartographer.Survey;

/// <summary>
/// What the survey adds to the engine's options (ADR 0018): the repository to read and how to read it. The
/// engine's own options (the output, the playbook, the models, the ceiling) are in <see cref="Conductor"/>.
/// </summary>
public sealed class SurveyOptions
{
    /// <summary>The repository to read. Never written to.</summary>
    public required string InputPath { get; init; }

    /// <summary>The engine's options; its output folder must not lie inside <see cref="InputPath"/>.</summary>
    public required ConductorOptions Conductor { get; init; }

    /// <summary>
    /// Files the person leaves out (ADR 0028): exact paths, folders, or patterns with *, ** and ?. Such a file is listed
    /// and never read; neither its text nor its name goes to a provider, and its text is not in the bundle.
    /// </summary>
    public IReadOnlyList<string> ExcludeFiles { get; init; } = [];

    /// <summary>Files the secret scan withheld that the person allows after all, by path (ADR 0028). The manifest says which rule each had matched.</summary>
    public IReadOnlyList<string> AllowFiles { get; init; } = [];

    /// <summary>Folders outside the input whose documents are read as part of it (ADR 0010: documents a person added).</summary>
    public IReadOnlyList<string> ExtraDocumentFolders { get; init; } = [];

    /// <summary>When documents count as thin, per the repository's nature (ADR 0010, 0016).</summary>
    public ThinDocumentThresholds Thin { get; init; } = new();

    /// <summary>Budgets that shape what one call of the survey sees.</summary>
    public SurveyBudgets Budgets { get; init; } = new();

    /// <summary>Run the code survey even when documents are not thin (ADR 0010).</summary>
    public bool ForceCodeSurvey { get; init; }

    /// <summary>A clone of the repository's older history, read as a second source of commits (ADR 0021); null asks when a gap is seen.</summary>
    public string? HistoryPath { get; init; }

    /// <summary>Write a draft ADR for every decision the judge confirms, not only the significant ones (open question 7; ADR 0006).</summary>
    public bool DraftRoutineDecisions { get; init; }
}

/// <summary>The survey playbook's models (ADR 0004): pro for the judgement-heavy phases, flash for the per-document passes; "vision" reads images (ADR 0014).</summary>
public static class SurveyModels
{
    public static PhaseModels Default { get; } = new()
    {
        Default = "mimo-v2.6-pro",
        ByPhase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["documents"] = "mimo-v2.6-flash",
            ["decisions"] = "mimo-v2.6-flash",
            ["verify"] = "mimo-v2.6-flash",
            ["vision"] = "mimo-v2.6-pro",
        },
    };
}

/// <summary>Below these, a repository's documents are thin (ADR 0010); they apply per nature (ADR 0016).</summary>
public sealed class ThinDocumentThresholds
{
    public int MinimumDocuments { get; init; } = 5;
    public long MinimumTextBytes { get; init; } = 20_000;
}

/// <summary>Budgets, in estimated tokens or characters, that shape one call of the survey.</summary>
public sealed class SurveyBudgets
{
    /// <summary>The most repository text (estimated tokens) one reader call carries; chunking cuts above it (ADR 0014).</summary>
    public int ReaderChunkTokens { get; init; } = 20_000;

    /// <summary>Lines carried over from the previous chunk.</summary>
    public int ChunkOverlapLines { get; init; } = 12;

    /// <summary>
    /// The most characters the overlap may take, whatever its lines: a ledger whose rows run to kilobytes each gave twelve
    /// lines of 141,000 characters before one chunk of Stomme's finished ledger, twice the chunk's budget, so the chunk
    /// held only overlap and its own rows were never read (2026-11-04).
    /// </summary>
    public int ChunkOverlapChars { get; init; } = 4_000;

    /// <summary>The most repository text (estimated tokens) one document call carries: smaller than a ledger's chunk, because the flash model's thinking on a 70 KB chunk ran past the provider's cut at about sixteen minutes.</summary>
    public int DocumentChunkTokens { get; init; } = 12_000;

    /// <summary>How many decision candidates one judge request carries (ADR 0006).</summary>
    public int DecisionsPerJudgeCall { get; init; } = 5;

    /// <summary>The most characters a document chunk's overlap carries, so that twelve lines of a table of long rows do not repeat 60 KB.</summary>
    public int DocumentOverlapChars { get; init; } = 4_000;

    /// <summary>The head of a document shown to the reader during orientation, in characters.</summary>
    public int HeadChars { get; init; } = 1_600;

    /// <summary>The most characters of document heads in one orientation batch.</summary>
    public int OrientationBatchChars { get; init; } = 60_000;

    /// <summary>The most a guide (CLAUDE.md, README, ...) contributes to the orientation call, in characters.</summary>
    public int GuideChars { get; init; } = 120_000;

    /// <summary>How many "look closer" requests the reader may make in one phase (ADR 0002), and their total size.</summary>
    public int LookCloserRequests { get; init; } = 6;
    public int LookCloserChars { get; init; } = 160_000;

    /// <summary>The size cap of the fixed-shape summary carried from chunk to chunk (ADR 0019), in characters.</summary>
    public int ChunkSummaryChars { get; init; } = 2_000;

    /// <summary>The size cap of the map digest in the prefix (ADR 0019), in characters.</summary>
    public int DigestChars { get; init; } = 6_000;

    /// <summary>How many rows one judge request checks (the plan, phase 1).</summary>
    public int JudgeRowsPerRequest { get; init; } = 10;

    /// <summary>The most characters of a row's detail kept on an item.</summary>
    public int DetailChars { get; init; } = 1_500;

    /// <summary>How many items the verify phase has the judge read against their ledger rows (the plan, phase 7); 0 asks none.</summary>
    public int VerifySampleItems { get; init; } = 10;

    /// <summary>How many links the verify phase has the judge read against the line that is their evidence; 0 asks none.</summary>
    public int VerifySampleLinks { get; init; } = 10;
}
