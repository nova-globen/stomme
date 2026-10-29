using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Pipelines
{
    /// <summary>
    /// The Pipelines module's permission keys (BL-95 increment (b)): checked on the SERVER through the D99
    /// bridge on every action - listing, authoring, publishing, starting and cancelling a run included. Every key
    /// defaults to Administrators (the framework adds that role to every declaration); an operator widens a key in the
    /// module definition's permission grid. No key is granted to Registered Users by default: a pipeline runs code.
    /// </summary>
    public static class PipelinesPermissions
    {
        public const string Resource = "pipelines";

        /// <summary>See the pipelines of the site, their runs and a run's log.</summary>
        public const string View = "pipelines:view";

        /// <summary>Create, edit, publish, retract and delete a pipeline; delete a run.</summary>
        public const string Manage = "pipelines:manage";

        /// <summary>Start a run of a published pipeline and cancel a running one.</summary>
        public const string Run = "pipelines:run";

        /// <summary>
        /// Decide a run's "Ask for approval" - approve or reject it. A key of its own, so the people who approve need
        /// not be the people who run or author pipelines.
        /// </summary>
        public const string Approve = "pipelines:approve";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage, Run, Approve };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new string[0]),
            (Manage, new string[0]),
            (Run, new string[0]),
            (Approve, new string[0]),
        };
    }
}
