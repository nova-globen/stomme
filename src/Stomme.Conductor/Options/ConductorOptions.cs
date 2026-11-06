using Stomme.Conductor.Playbooks;

namespace Stomme.Conductor.Options;

/// <summary>
/// Everything configurable in a run of the engine (ADR 0001: every behaviour the owner asked to be configurable
/// is a library option; a host only maps its switches to these). Paths are absolute. The engine's only writes
/// are inside <see cref="OutputPath"/>; what a host reads is the host's own option.
/// </summary>
public sealed class ConductorOptions
{
    /// <summary>The output folder: the log, the state, the cache and the questions, with the host's own output beside them.</summary>
    public required string OutputPath { get; init; }

    /// <summary>The playbook folder (ADR 0018): the phases, their prompts and shapes, the judge's questions, the data tables. Null means <see cref="Playbook"/> is used.</summary>
    public string? PlaybookPath { get; init; }

    /// <summary>A playbook the host loaded itself, such as one embedded in its package (ADR 0022); used when <see cref="PlaybookPath"/> is null.</summary>
    public Playbook? Playbook { get; init; }

    /// <summary>The generative reader's connection (ADR 0004).</summary>
    public ReaderOptions Reader { get; init; } = new();

    /// <summary>The judge's connection (ADR 0004, 0013).</summary>
    public JudgeOptions Judge { get; init; } = new();

    /// <summary>Which model each phase asks (ADR 0004: configuration, not code); a host sets its playbook's defaults.</summary>
    public PhaseModels Models { get; init; } = new();

    /// <summary>The ceiling and the price table (ADR 0008).</summary>
    public SpendingOptions Spending { get; init; } = new();

    /// <summary>The judge's yes and no thresholds (ADR 0013).</summary>
    public JudgeThresholds Thresholds { get; init; } = new();

    /// <summary>The engine's own budgets.</summary>
    public BudgetOptions Budgets { get; init; } = new();

    /// <summary>Stop cleanly after this phase (a console's --until), or null to run every phase.</summary>
    public string? StopAfterPhase { get; init; }

    /// <summary>Phases whose units of work are forgotten this run, so they run again (names, or "*" for all).</summary>
    public IReadOnlySet<string> RerunPhases { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Units of work whose key starts with one of these are forgotten this run, so they run again (a console's --rerun-unit).</summary>
    public IReadOnlyList<string> RerunUnits { get; init; } = [];

    /// <summary>Start over: keep the old state renamed and begin a new one (ADR 0017's --fresh); the default resumes.</summary>
    public bool Fresh { get; init; }
}

/// <summary>Which protocol the reader speaks.</summary>
public enum ReaderProtocol
{
    /// <summary>The OpenAI SDK against an OpenAI-compatible endpoint (MiMo's /v1).</summary>
    OpenAiCompatible,

    /// <summary>The official Anthropic SDK (Claude, or MiMo's /anthropic).</summary>
    AnthropicCompatible,
}

/// <summary>Whether the reader's model thinks before it answers: the provider's default, on, or off (cheaper and faster where it holds).</summary>
public enum ThinkingMode
{
    Default,
    On,
    Off,
}

/// <summary>The reader's connection. The key is read from the environment variable named here, never held here.</summary>
public sealed class ReaderOptions
{
    public ReaderProtocol Protocol { get; init; } = ReaderProtocol.OpenAiCompatible;

    public string Endpoint { get; init; } = "https://api.xiaomimimo.com/v1";

    /// <summary>The key itself, given by the host, which reads it from wherever it keeps keys (ADR 0004, 0022); never logged.</summary>
    public string? Key { get; init; }

    /// <summary>
    /// How long one attempt may take before it is cut and retried. A thinking model over a large prompt is slow:
    /// the orientation call on Stomme took nine minutes on MiMo pro (2026-11-03), and a cut attempt may still be
    /// billed by the provider, so this is generous.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>After this long without an answer the log says the call is still in flight, then again every five times this; zero turns the notices off.</summary>
    public TimeSpan WaitingNotice { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How many reader calls run at once (MiMo allows 100 requests a minute).</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>Retries on a transport failure, a timeout or a 429, with a growing wait; every retry is logged.</summary>
    public int Retries { get; init; } = 3;

    /// <summary>The wait before the first retry; each later retry waits twice the one before.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(4);

    public float Temperature { get; init; } = 0.1f;

    /// <summary>The output budget of one call. MiMo v2.6 allows 128K.</summary>
    public int MaxOutputTokens { get; init; } = 65_536;

    /// <summary>The chain of thought on every call unless a request says otherwise; MiMo thinks by default, and its thinking is most of a call's cost and nearly all of its time.</summary>
    public ThinkingMode Thinking { get; init; } = ThinkingMode.Default;

    /// <summary>
    /// How many output tokens a second an attempt is assumed to have generated when it is cut before any usage
    /// reaches the engine, so that the spend book never undercounts what the provider bills for it. MiMo flash
    /// produced 51,235 tokens in eleven minutes of thinking on 2026-11-04, about 78 a second.
    /// </summary>
    public double LostAttemptOutputTokensPerSecond { get; init; } = 80;
}

/// <summary>The judge's connection (TypeSafe Jev). Modelled on Fravashi's broker adapter.</summary>
public sealed class JudgeOptions
{
    public string Endpoint { get; init; } = "https://api.typesafe.ai/v1/systemone";

    public string Model { get; init; } = "jev-latest";

    /// <summary>The key itself, given by the host, which reads it from wherever it keeps keys (ADR 0004, 0022); never logged.</summary>
    public string? Key { get; init; }

    /// <summary>How long one call may take, retries included. Judge states here are chunks, larger than the broker's prompts.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The wait before the one retry a 429 or 529 earns.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How many judge calls run at once.</summary>
    public int Concurrency { get; init; } = 6;

    /// <summary>The most characters one judge state carries (Jev reads 32k tokens per request; a markdown table runs near 3 characters per token, and 72,000 leaves room for the questions, where 90,000 was refused as too long).</summary>
    public int MaxStateChars { get; init; } = 72_000;

    /// <summary>Allow an http endpoint, for a stub under test only.</summary>
    public bool AllowInsecureEndpoint { get; init; }
}

/// <summary>
/// The model each phase asks (ADR 0004). A host sets the defaults for its playbook's phases, and may name a
/// pseudo-phase (such as "vision") for a model used across phases; a phase not named here asks the default.
/// </summary>
public sealed class PhaseModels
{
    public string Default { get; init; } = "mimo-v2.6-pro";

    /// <summary>Models by phase name.</summary>
    public IReadOnlyDictionary<string, string> ByPhase { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string For(string phase) => phase is not null && ByPhase.TryGetValue(phase, out var model) ? model : Default;

    /// <summary>Every phase on one model (a console's --model).</summary>
    public static PhaseModels All(string model) => new() { Default = model };

    /// <summary>What is configured, for a manifest: "default", then each named phase.</summary>
    public IEnumerable<KeyValuePair<string, string>> Configured()
    {
        yield return new KeyValuePair<string, string>("default", Default);
        foreach (var pair in ByPhase.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            yield return pair;
        }
    }
}

/// <summary>What the ceiling applies to (ADR 0008).</summary>
public enum CeilingScope
{
    /// <summary>This run alone.</summary>
    Run,

    /// <summary>Every run recorded in the spend book together.</summary>
    AllRuns,
}

/// <summary>The spending ceilings and the price table (ADR 0008, 0024).</summary>
public sealed class SpendingOptions
{
    /// <summary>The reader's ceiling in US dollars, at which the run asks whether to continue (ADR 0024: $40 in total).</summary>
    public decimal CeilingUsd { get; init; } = 40m;

    /// <summary>The judge's limit in US dollars (ADR 0024: $5, a hard limit); at it the run asks rather than guesses, since a failed judgment is never an answer.</summary>
    public decimal JudgeCeilingUsd { get; init; } = 5m;

    public CeilingScope Scope { get; init; } = CeilingScope.AllRuns;

    /// <summary>Where the spend of every run is kept (ADR 0008). Null means spend.json in the output folder, next to the cache.</summary>
    public string? SpendPath { get; init; }

    /// <summary>Prices per model, with the date they were read.</summary>
    public PriceTable Prices { get; init; } = PriceTable.Default;
}

/// <summary>The judge's thresholds (ADR 0013): at or above Yes is yes, at or below No is no, between is unsettled.</summary>
public sealed class JudgeThresholds
{
    public double Yes { get; init; } = 0.7;
    public double No { get; init; } = 0.3;
}

/// <summary>The engine's budgets, in estimated tokens.</summary>
public sealed class BudgetOptions
{
    /// <summary>The glossary's budget (ADR 0017: 10k to 20k to start).</summary>
    public int GlossaryTokens { get; init; } = 12_000;
}
