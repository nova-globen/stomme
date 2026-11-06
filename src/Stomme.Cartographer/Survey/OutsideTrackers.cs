using System.Text.RegularExpressions;

namespace Stomme.Cartographer.Survey;

/// <summary>
/// Places outside a repository where its work is tracked, named by an address in its guides (the verdict's redo,
/// change 12): found by code, by the address's shape. An issue list or a board counts; one issue or one pull
/// request of a public host does not, since a guide cites those of other projects too.
/// </summary>
public static partial class OutsideTrackers
{
    public static IEnumerable<string> In(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (Match match in Tracker().Matches(line))
        {
            yield return match.Value.TrimEnd('.', ',', ';', ')', '/');
        }
    }

    [GeneratedRegex(@"https?://(?:www\.)?(?:github\.com/[\w.-]+/[\w.-]+/(?:issues(?![/\w]*\d)|projects(?:/\d+)?)|gitlab\.com/[\w./-]+?/-/(?:issues(?!/\d)|boards)|[\w-]+\.atlassian\.net/(?:jira/|browse/|projects/)[\w/.-]*|linear\.app/[\w-]+(?:/[\w-]+)*|trello\.com/b/[\w-]+(?:/[\w-]+)?|dev\.azure\.com/[\w.-]+/[\w.%-]+/_(?:workitems|boards|backlogs)[\w/.-]*|[\w-]+\.youtrack\.cloud[\w/.-]*|app\.asana\.com/[\w/-]+|app\.shortcut\.com/[\w/-]+|[\w-]+\.monday\.com/boards/\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tracker();
}
