namespace Stomme.Engine.Pipelines.UIHints.RadioList;

/// <summary>
/// Provides properties for the <see cref="InputUIHints.RadioList"/> UI hint.
/// </summary>
public class RadioList
{
    /// <summary>
     /// The radio list.
     /// </summary>
    public IEnumerable<RadioListItem> Items { get; set; } = null!;

    /// <summary>
    /// The name of the provider that will provide the select list.
    /// </summary>
    public bool IsFlagsEnum { get; set; }
}