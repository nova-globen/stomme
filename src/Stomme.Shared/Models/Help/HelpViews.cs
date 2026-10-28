#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Models.Help
{
    /// <summary>
    /// A topic as its AUTHOR sees it (BL-84): the Markdown itself, and whether a contributor still owns it. Only the
    /// <c>help:author</c>-gated read returns this - a reader never receives Markdown, only the server-rendered
    /// <c>SafeHtml</c> of a published topic.
    /// </summary>
    /// <param name="HasContributedSeed">
    /// True when a contributor currently ships this anchor and locale, so "reset to seed" has something to reset to.
    /// </param>
    public sealed record HelpTopicEditView(
        Guid Id,
        string Anchor,
        string Locale,
        string Title,
        string Markdown,
        bool IsPublished,
        bool IsSeeded,
        string? SeedContributorId,
        int ContentVersion,
        bool HasContributedSeed);

    /// <summary>
    /// A new topic (<see cref="Id"/> null) or an edit of one. No site, tenant or user: all three are resolved
    /// server-side, so there is nothing to forge. An edit makes the topic the SITE's - no contributor updates it again.
    /// </summary>
    public sealed record HelpTopicSaveInput(Guid? Id, string Anchor, string Locale, string Title, string Markdown);

    /// <summary>Unsaved Markdown to render - server-side, through the same pipeline and allowlist a reader gets.</summary>
    public sealed record HelpPreviewInput(string Markdown);

    public sealed record HelpPreviewResult(string SafeHtml);

    /// <summary>
    /// A contribution the registry REJECTED (a key shipped twice, a contributor id claimed twice, a broken contributor)
    /// - listed on the admin page so an operator can find the package, since nothing was served for it.
    /// </summary>
    public sealed record HelpConflictView(string Kind, string Name, IReadOnlyList<string> Contributors, string Reason);
}
