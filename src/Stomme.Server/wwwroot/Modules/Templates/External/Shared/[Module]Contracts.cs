using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Shared;

namespace [Owner].Module.[Module]
{
    /// <summary>
    /// The module's permission KEYS (D99): <c>resource:operation</c>, lower-case, declared once here and used by the requests'
    /// <c>[Requires]</c>, the controller's <c>[RequiresPermissionKey]</c> and the page's <c>IsAuthorizedPermission</c>. The
    /// client's <c>ModuleInfo</c> declares them, with the roles a new site grants them to; a key no module declares denies
    /// everyone. Every module definition of the package that uses a key must declare it - a placement carries the site's key
    /// rows only when its own definition declares them.
    /// </summary>
    public static class [Module]Permissions
    {
        public const string Resource = "[ModuleKey]";

        /// <summary>Read the entries.</summary>
        public const string View = "[ModuleKey]:view";

        /// <summary>Add entries.</summary>
        public const string Edit = "[ModuleKey]:edit";

        /// <summary>The keys and the roles a new site grants them to. Registered users read; administrators write.</summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { RoleNames.Registered, RoleNames.Admin }),
            (Edit, new[] { RoleNames.Admin }),
        };
    }
}

namespace [Owner].Module.[Module].Models
{
    /// <summary>An entry as the API and the pages see it.</summary>
    public sealed record [Module]EntryModel(Guid Id, string Text, int AuthorUserId, DateTime CreatedUtc);

    /// <summary>What a caller sends to add an entry.</summary>
    public sealed class Add[Module]EntryRequest
    {
        public string Text { get; set; } = string.Empty;
    }
}

namespace [Owner].Module.[Module].Services
{
    using [Owner].Module.[Module].Models;

    /// <summary>
    /// The module's service contract. The browser's implementation calls the API over HTTP; on the server a second
    /// implementation sends the requests directly - pages consume this interface and never a repository.
    /// </summary>
    public interface I[Module]Service
    {
        Task<List<[Module]EntryModel>> ListEntriesAsync();

        Task<[Module]EntryModel> AddEntryAsync(Add[Module]EntryRequest request);
    }
}
