namespace Stomme.Conductor.Policies;

/// <summary>How a run backs up what it reads (ADR 0009): a policy the engine applies and the host fills with its own archive.</summary>
public enum BackupKind
{
    /// <summary>The host zips what the run reads into the output folder before the run.</summary>
    Zip,

    /// <summary>Ask through the question policy; the default is to zip.</summary>
    Ask,

    /// <summary>No backup; the state still records a hash per input.</summary>
    None,
}

/// <summary>What a backup produced.</summary>
public sealed record BackupResult(string? ArchivePath, int Entries, long Bytes, string Note);

/// <summary>The question the ask policy puts through the question policy.</summary>
public static class BackupQuestion
{
    public static Question Build() => new(
        "backup:zip",
        "backup",
        "Zip the inputs the run reads into the output folder before the run?",
        [new QuestionOption("yes", "zip them under backup/"), new QuestionOption("no", "no zip; the state still records a hash per input")],
        "yes (the zip is taken)");
}
