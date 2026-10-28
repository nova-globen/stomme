namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents the switch mode.
/// </summary>
[Flags]
public enum TypeKind
{
    Unknown = 0,
    Primitive = 1,
    Object = 2,
    Step = 4,
    Trigger = 8
}