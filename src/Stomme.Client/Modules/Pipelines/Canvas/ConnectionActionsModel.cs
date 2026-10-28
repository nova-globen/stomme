using System;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// BL-229 - n8n's buttons on a connection: a step put between its two steps, or the connection removed. Drawn by
    /// <see cref="ConnectionActions"/> as the connection's second label, on its middle, and shown - in place of the outcome's
    /// name, as n8n does - while the pointer is over the connection or it is selected. Only an author's canvas has them.
    /// </summary>
    public sealed class ConnectionActionsModel : LinkLabelModel
    {
        public ConnectionActionsModel(BaseLinkModel parent, Action<BaseLinkModel> insert, Action<BaseLinkModel> remove)
            : base(parent, "", 0.5, null)
        {
            Insert = insert;
            Remove = remove;
        }

        public Action<BaseLinkModel> Insert { get; }

        public Action<BaseLinkModel> Remove { get; }
    }
}
