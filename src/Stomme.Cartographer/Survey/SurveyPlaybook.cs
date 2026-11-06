using System.Reflection;
using Stomme.Conductor.Playbooks;

namespace Stomme.Cartographer.Survey;

/// <summary>
/// The survey playbook as the Cartographer package carries it (ADR 0022): playbooks/survey-repository embedded
/// at build time, so a consumer of the package needs no folder. A folder given as an override wins; the console
/// uses one when it finds it next to the executable or the working folder, so a prompt can be edited without a build.
/// </summary>
public static class SurveyPlaybook
{
    private const string Prefix = "playbook/";

    public static string Name => "survey-repository";

    public static Playbook Embedded()
    {
        var assembly = typeof(SurveyPlaybook).Assembly;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"The embedded playbook entry {resource} could not be opened.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            entries[resource[Prefix.Length..].Replace('\\', '/')] = memory.ToArray();
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The Cartographer package carries no embedded playbook.");
        }

        return Playbook.FromEntries("embedded", entries);
    }
}
