namespace Stomme.Conductor.Keys;

/// <summary>Where a key was found. Recorded in the log; the key itself never is.</summary>
public enum KeySource
{
    NotFound,
    Process,

    /// <summary>The Windows user scope, read because a process started before a variable was set does not see it (ADR 0004).</summary>
    UserScope,
}

/// <summary>
/// Reads an API key from the environment variable whose NAME is configuration (ADR 0004). The value is handed
/// to the adapter and nowhere else: never printed, logged, written, or passed on a command line.
/// </summary>
public static class KeyReader
{
    public static (string? Key, KeySource Source) Read(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return (value.Trim(), KeySource.Process);
        }

        if (OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (value.Trim(), KeySource.UserScope);
            }
        }

        return (null, KeySource.NotFound);
    }
}
