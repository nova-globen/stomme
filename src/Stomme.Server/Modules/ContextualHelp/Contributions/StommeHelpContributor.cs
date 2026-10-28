#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Help;

namespace Stomme.Modules.ContextualHelp.Contributions
{
    /// <summary>
    /// The framework's own help topics (BL-84, D103 item 2): English topics for the admin dashboard page and the five
    /// admin controls an operator meets first, so that an install with <c>Help:Enabled</c> has something to show and
    /// the feature can be DRIVEN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is found by the same convention as a third-party package's contributor</b> - a public parameterless
    /// constructor in a <c>Stomme</c> assembly - and it lands in the same registry, so its keys are checked for
    /// collision exactly like anyone else's. The framework is not privileged: a package shipping one of these keys
    /// costs BOTH sides the key (BL-105's rule), which is why nothing here is load-bearing.
    /// </para>
    /// <para>
    /// The control anchors are derived from <c>typeof</c>, not typed out, so renaming a control cannot silently orphan
    /// its topic - it moves the anchor with it, and the stored row simply stops matching (a new one is seeded).
    /// </para>
    /// </remarks>
    public sealed class StommeHelpContributor : IHelpTopicContributor
    {
        /// <summary>What every row this contributor seeds records as its owner. Changing it orphans every seeded row.</summary>
        public const string Id = "stomme";

        /// <summary>
        /// A FIELD anchor - the Site Settings render-mode choice, referenced by <c>Label HelpTopic</c>. A field anchor is a
        /// name, not a type: it survives the control being refactored, and <c>HelpAnchorCoverageTests</c> fails the build
        /// if a label names one that no contributor ships.
        /// </summary>
        public const string SiteRenderModeAnchor = "stomme:site.rendermode";

        public string ContributorId => Id;

        public IReadOnlyCollection<HelpTopicContribution> Topics { get; } = Build();

        /// <summary>The anchor of an admin control, exactly as the launcher will derive it from the page's ModuleType.</summary>
        public static string ControlAnchor(Type control) =>
            HelpKeys.TryModuleAnchor(control.FullName, out var anchor)
                ? anchor
                : throw new InvalidOperationException($"{control.FullName} cannot be a help anchor.");

        private static IReadOnlyCollection<HelpTopicContribution> Build()
        {
            HelpKeys.TryPageAnchor("admin", out var dashboard);

            return new[]
            {
                new HelpTopicContribution(dashboard, "en", "Administration",
                    "This is the **administration dashboard** for this site. Each tile opens one area of the site's " +
                    "configuration.\n\n" +
                    "- **Site Settings** - the site's name, theme, logo and rendering options.\n" +
                    "- **Page Management** - the pages, their order and their permissions.\n" +
                    "- **User Management** - accounts, roles and sign-in.\n" +
                    "- **Module Management** and **Theme Management** - what is installed.\n\n" +
                    "What you can see here depends on your roles."),

                new HelpTopicContribution(ControlAnchor(typeof(Stomme.Modules.Admin.Site.Index)), "en", "Site Settings",
                    "Settings that apply to the **whole site**, grouped into sections: *Theme*, *Appearance*, " +
                    "*Functionality*, *Page Content*, *SMTP Settings* and more. A host also sees the *Hosting Model*, the " +
                    "site's *Urls* and its *Database*.\n\n" +
                    "Changes take effect when you **Save**. The hosting options change how every page is served, so check " +
                    "a public page after changing them."),

                new HelpTopicContribution(ControlAnchor(typeof(Stomme.Modules.Admin.Pages.Index)), "en", "Page Management",
                    "Every page of this site, in navigation order.\n\n" +
                    "- **Add Page** creates a page; its *path* is the address it is served at.\n" +
                    "- A page's **permissions** decide who can view and who can edit it.\n" +
                    "- A deleted page goes to the **recycle bin** and can be restored from there."),

                new HelpTopicContribution(ControlAnchor(typeof(Stomme.Modules.Admin.Users.Index)), "en", "User Management",
                    "The accounts registered on this site.\n\n" +
                    "- **Add User** creates an account directly.\n" +
                    "- **Roles** on a user's row decides what they can see and do.\n" +
                    "- A host also sees a **Settings** tab, which controls registration, passwords, lockout and external sign-in."),

                new HelpTopicContribution(ControlAnchor(typeof(Stomme.Modules.Admin.ModuleDefinitions.Index)), "en", "Module Management",
                    "The modules installed on this host. A module is placed on a page from that page's *edit mode*; this " +
                    "list is where one is installed (**Install Module**) and managed (**Edit**).\n\n" +
                    "A module with module-wide settings shows a **Configure** tab when you edit it."),

                new HelpTopicContribution(SiteRenderModeAnchor, "en", "Render mode",
                    "How this site's pages are produced.\n\n" +
                    "- **Static** - each page is rendered once on the server and sent as HTML. The lightest choice for " +
                    "public pages; a form posts back and the page reloads.\n" +
                    "- **Interactive** - pages stay live after they load, so controls respond without a full page load.\n" +
                    "- **Headless** - Nova Globen Stomme does not render the site's pages; another front end presents its content.\n\n" +
                    "Administration controls stay interactive even on a Static site. The choice applies to every page of " +
                    "the site, so check a public page after changing it."),

                new HelpTopicContribution(ControlAnchor(typeof(Stomme.Modules.Admin.Themes.Index)), "en", "Theme Management",
                    "The themes installed on this host (**Install Theme** adds one). A theme decides how pages look; the " +
                    "site's default theme is chosen under *Site Settings*, and a single page can override it."),
            };
        }
    }
}
