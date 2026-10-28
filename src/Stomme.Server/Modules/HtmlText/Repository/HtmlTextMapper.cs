using Stomme.Documentation;

namespace Stomme.Modules.HtmlText.Repository
{
    /// <summary>
    /// Translates between <see cref="HtmlTextAggregate"/> (the persisted aggregate) and
    /// <see cref="Models.HtmlText"/> (the client/server wire contract) at the <see cref="HtmlTextRepository"/>
    /// boundary — the module's twin of <c>PermissionMapper</c> (D24/D55). Only the columns the harvested schema
    /// actually has are mapped.
    /// </summary>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public static class HtmlTextMapper
    {
        public static Models.HtmlText ToModel(HtmlTextAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Models.HtmlText
            {
                HtmlTextId = aggregate.Id,
                ModuleId = aggregate.ModuleId,
                Content = aggregate.Content,
                CreatedBy = aggregate.CreatedBy,
                CreatedOn = aggregate.CreatedOn,
                ModifiedBy = aggregate.ModifiedBy,
                ModifiedOn = aggregate.ModifiedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for a delete, which addresses an existing row by its id.
        /// </summary>
        public static HtmlTextAggregate ToAggregate(Models.HtmlText htmlText, bool assignId)
        {
            var aggregate = new HtmlTextAggregate
            {
                ModuleId = htmlText.ModuleId,
                Content = htmlText.Content,
                CreatedBy = htmlText.CreatedBy,
                CreatedOn = htmlText.CreatedOn,
                ModifiedBy = htmlText.ModifiedBy,
                ModifiedOn = htmlText.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(htmlText.HtmlTextId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit stamps
        /// applied by <c>DbContextUtils</c> on the relational path / the store audit listener on the document path),
        /// so the caller's <see cref="Models.HtmlText"/> reflects the persisted row — the harvested repository
        /// handed back the tracked entity itself and <c>ServerHtmlTextService.AddHtmlTextAsync</c> logs it.
        /// </summary>
        public static void CopyPersistedValues(HtmlTextAggregate aggregate, Models.HtmlText htmlText)
        {
            htmlText.HtmlTextId = aggregate.Id;
            htmlText.CreatedBy = aggregate.CreatedBy;
            htmlText.CreatedOn = aggregate.CreatedOn;
            htmlText.ModifiedBy = aggregate.ModifiedBy;
            htmlText.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
