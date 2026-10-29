using System.Collections.Generic;
using System.Linq;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Themes.Controls
{
    public class MenuBase : ThemeControlBase
    {
        protected IEnumerable<Page> MenuPages => GetMenuPages().ToList();

        // Narrow-viewport menu state. The toggler used to be a pure data-bs-toggle="collapse"
        // button; the Bootstrap JS that drove it went away with the CDN in Phase 1, so below the
        // breakpoint the menu could never be opened at all. Stomme owns this chrome now.
        //
        // Dual-mode, like Section/TabStrip: when the theme renders interactively the toggler is a
        // real <button> driven from Blazor state (and menu links close the menu again, which is
        // what the inert `data-bs-target=".navbar-collapse.show"` spans used to do). When the theme
        // renders statically there is no circuit to click, so the markup falls back to a
        // checkbox+label, which needs no script at all. A static navigation is a full page load, so
        // the menu resets closed by itself and never needs dismissing.
        protected bool Open { get; private set; }

        protected bool Interactive => PageState.RenderMode == RenderModes.Interactive;

        protected void ToggleMenu() => Open = !Open;

        protected void CloseMenu() => Open = false;

        protected string GetTarget(Page page)
        {
            return page.Url != null && page.Url.StartsWith("http") ? "_new" : string.Empty;
        }

        protected string GetUrl(Page page)
        {
            if (page.IsClickable)
            {
                return string.IsNullOrEmpty(page.Url) ? NavigateUrl(page.Path) : page.Url;
            }
            else
            {
                return "javascript:void(0)";
            }
        }

        private IEnumerable<Page> GetMenuPages()
        {
            var securityLevel = int.MaxValue;
            foreach (Page p in PageState.Pages.Where(item => item.IsNavigation))
            {
                if (p.Level <= securityLevel && UserSecurity.IsAuthorized(PageState.User, PermissionNames.View, p.PermissionList))
                {
                    securityLevel = int.MaxValue;
                    yield return p;
                }
                else
                {
                    if (securityLevel == int.MaxValue)
                    {
                        securityLevel = p.Level;
                    }
                }
            }
        }
    }
}
