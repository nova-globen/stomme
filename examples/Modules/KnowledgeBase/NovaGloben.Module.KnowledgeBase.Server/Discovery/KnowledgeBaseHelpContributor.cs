#nullable enable
using System.Collections.Generic;
using Stomme.Help;

namespace NovaGloben.Module.KnowledgeBase.Discovery
{
    /// <summary>
    /// The module's help topics (D103): one per page it places and one per control a reader meets, in English. Discovered by
    /// the host's convention - a public parameterless constructor in a <c>*.Stomme</c> assembly - while contextual help is
    /// on (<c>Help:Enabled</c>), and reconciled into every site as an editable row: an operator's edit survives, and the
    /// contributor id is what the row remembers so no other package can overwrite it.
    /// </summary>
    /// <remarks>
    /// A PAGE anchor is <c>page:</c> and the page's path; a CONTROL anchor is the control's type name, lower-cased
    /// (<see cref="HelpKeys.TryModuleAnchor"/>). The Server assembly does not reference the Client one, so the control
    /// anchors are written as the names they are - the drive resolves each one through the host's help API.
    /// </remarks>
    public sealed class KnowledgeBaseHelpContributor : IHelpTopicContributor
    {
        public const string Id = "nova-globen.knowledge-base";

        public const string PageAnchor = "page:knowledge-base";

        public const string CategoriesPageAnchor = "page:knowledge-base/categories";

        public const string EditAnchor = "novagloben.module.knowledgebase.edit";

        public string ContributorId => Id;

        public IReadOnlyCollection<HelpTopicContribution> Topics { get; } = new[]
        {
            new HelpTopicContribution(PageAnchor, "en", "The knowledge base",
                "The site's **knowledge base**: articles that answer the questions its visitors ask.\n\n" +
                "- Open an article from the list; its address stays the same when it is edited, so it can be shared.\n" +
                "- **Categories** groups the articles; choose one to see only its articles.\n" +
                "- The site's search finds the published articles too.\n\n" +
                "An editor also sees **New article** and each draft; a draft is not shown to anyone else until it is published."),

            new HelpTopicContribution(CategoriesPageAnchor, "en", "Knowledge base categories",
                "The knowledge base's **categories**. Each one lists its published articles.\n\n" +
                "An editor adds one here with **Create**; its address is made from its name."),

            new HelpTopicContribution(EditAnchor, "en", "Writing an article",
                "Write the article's **title** and **body**, and choose its **category**.\n\n" +
                "- **Save draft** keeps it as a draft, which only editors see; **Save** keeps later changes.\n" +
                "- **Publish** shows it to every visitor, in the site's search and in its sitemap. An article is published once.\n" +
                "- The article's **address** is made from its title unless you write one; it does not change afterwards."),
        };
    }
}
