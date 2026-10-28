using System.Collections.Generic;

namespace Stomme.Models
{
    /// <summary>
    /// One custom scope — an API/resource identifier a client may request and a resource server validates.
    /// Both the declarative <c>OpenIddict:Scopes</c> config shape and the admin UI's wire shape.
    /// </summary>
    public class OpenIddictScopeSeed
    {
        public string Name { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        /// <summary>Resource identifiers (audiences) tokens carrying this scope are valid for.</summary>
        public List<string> Resources { get; set; } = new List<string>();
    }
}
