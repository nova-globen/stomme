namespace Stomme.Cartographer.Sources;

/// <summary>What a file is by its extension and bytes, before any model reads it (the playbook's file table, ADR 0016).</summary>
public enum FileCategory
{
    /// <summary>Prose a decoder turns into text: markdown, text, html, docx, pdf, and the like.</summary>
    Document,

    /// <summary>Source code in a language the playbook's table knows.</summary>
    Code,

    /// <summary>Structured data: json, csv, yaml, xml, sql.</summary>
    Data,

    /// <summary>Build and tool configuration.</summary>
    Config,

    /// <summary>A raster image the reader's vision can read.</summary>
    Image,

    /// <summary>A design file: svg (also text), figma, sketch, psd, and the like.</summary>
    Design,

    /// <summary>An archive or package.</summary>
    Archive,

    /// <summary>Bytes no decoder reads; catalogued only.</summary>
    Binary,

    /// <summary>Text with an extension the table does not know; the reader says what it is.</summary>
    TextUnknown,
}

/// <summary>One file of the input, as found (ADR 0009: a hash per file read).</summary>
public sealed record SourceFile(
    string Path,
    string AbsolutePath,
    long Size,
    string Extension,
    FileCategory Category,
    string? Language,
    bool IsText,
    string Sha256,
    bool Tracked)
{
    /// <summary>
    /// Why the file is not read, or null when it is: a backup copy, a file that looks like it holds a secret, a file
    /// the person excluded. Such a file is listed in the manifest with this reason, and its content reaches neither
    /// a provider nor the bundle: no decoder reads it, no scan opens it, the backup zip leaves it out.
    /// </summary>
    public string? NotRead { get; init; }

    /// <summary>What the secret scan found in the file, whether it is withheld or a person allowed it by its path; null when nothing.</summary>
    public SecretHit? Secret { get; init; }

    /// <summary>The person's pattern that leaves the file out, or null. Such a file's name is not shown to a provider either.</summary>
    public string? ExcludedBy { get; init; }

    /// <summary>The kind of reason in <see cref="NotRead"/>, without its detail: "a backup copy", "it looks like it holds a secret", "excluded by the person".</summary>
    public string? NotReadKind => NotRead is null ? null : NotRead.Split(':', '(')[0].Trim();
}

/// <summary>Where a scan came from and how the files were listed.</summary>
public sealed record RepositorySnapshot(
    string Root,
    IReadOnlyList<SourceFile> Files,
    GitInfo? Git,
    string FilesHash,
    string ListedBy,
    IReadOnlyList<string> Notes);
