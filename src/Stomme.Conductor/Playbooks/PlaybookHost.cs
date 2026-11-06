using System.Text;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

namespace Stomme.Conductor.Playbooks;

/// <summary>A phase of a playbook, built by the host over the run's context: runs its units of work over the state, skipping the ones already done (ADR 0017).</summary>
public interface IPhase
{
    string Name { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What a use of the engine supplies to a run (ADR 0018): its inputs, its phases, its backup, its own output
/// and its lines of state.md. The engine calls these in that order and knows nothing of what the inputs are: a
/// host lists them by key and hash, and the state keeps them that way.
/// </summary>
public interface IPlaybookHost
{
    /// <summary>
    /// Called once the log, the state, the models and the policies are ready, before anything else runs: the
    /// host describes its source (<see cref="RunContext.DescribeSource"/>), reconciles its inputs with the
    /// state (<see cref="RunContext.ReconcileInputs"/>) and returns its phases. A phase the playbook names and
    /// the host does not return is logged as not built and skipped.
    /// </summary>
    Task<IReadOnlyList<IPhase>> PrepareAsync(RunContext context, CancellationToken cancellationToken);

    /// <summary>Backs up the inputs when the backup policy says so (ADR 0009): what it produced, or null when there was nothing to archive.</summary>
    Task<BackupResult?> BackUpAsync(RunContext context, CancellationToken cancellationToken);

    /// <summary>Writes the host's own output at the end of a run, whatever the outcome; the engine has written the state's run row, the questions and the log by then.</summary>
    void Finish(RunContext context, BackupResult? backup, RunOutcome outcome, string message);

    /// <summary>Appends the host's sections of state.md, rendered while a run is going (ADR 0017); it may be called before <see cref="PrepareAsync"/> has finished.</summary>
    void DescribeState(RunContext context, StringBuilder text);
}
