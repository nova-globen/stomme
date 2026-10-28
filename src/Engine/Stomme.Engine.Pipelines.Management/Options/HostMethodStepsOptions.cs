namespace Stomme.Engine.Pipelines.Management.Options;

/// <summary>
/// Represents the options for managing host method-based steps in pipelines.
/// </summary>
public class HostMethodStepsOptions
{
    /// <summary>
    /// Maps a registration key to a CLR type whose public async methods should be exposed as steps.
    /// </summary>
    public IDictionary<string, Type> StepTypes { get; } = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a new step type to the collection of step types.
    /// </summary>
    /// <typeparam name="T">The type of the step to add.</typeparam>
    /// <param name="key">An optional key to associate with the step type. If not provided, the type's name will be used.</param>
    public HostMethodStepsOptions AddType<T>(string? key = null) where T : class
    {
        key ??= typeof(T).Name;
        StepTypes[key] = typeof(T);
        return this;
    }

    /// <summary>
    /// Adds a new step type to the collection of step types.
    /// </summary>
    /// <param name="type">The type of the step to add.</param>
    /// <param name="key">An optional key to associate with the step type. If not provided, the type's name will be used.</param>
    public HostMethodStepsOptions AddType(Type type, string? key = null)
    {
        key ??= type.Name;
        StepTypes[key] = type;
        return this;
    }
}
