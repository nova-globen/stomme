using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Components;

using Stomme.Models;
using Stomme.UI;

namespace Stomme.Themes.Controls
{
    public abstract class MenuItemsBase : MenuBase
    {
        [Parameter()]
        public Page ParentPage { get; set; }

        [Parameter()]
        public IEnumerable<Page> Pages { get; set; }

        /// <summary>
        /// Raised when a menu link is activated, so an interactively-rendered menu can close itself
        /// again (D18). This replaces the inert `data-bs-target=".navbar-collapse.show"` spans that
        /// used to wrap every link and dismiss the menu through Bootstrap's JS.
        /// </summary>
        [Parameter()]
        public EventCallback OnNavigate { get; set; }

        protected IEnumerable<Page> GetChildPages()
        {
            return Pages
                .Where(e => e.ParentId == ParentPage?.PageId)
                .OrderBy(e => e.Order)
                .AsEnumerable();
        }
    }
}
