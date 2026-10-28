using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Serialization;

/// <summary>
/// Compares two <see cref="Connection"/> instances.
/// </summary>
public class ConnectionComparer : IEqualityComparer<Connection>
{
    /// <inheritdoc />
    public bool Equals(Connection? x, Connection? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (ReferenceEquals(x, null)) return false;
        if (ReferenceEquals(y, null)) return false;
        if (x.GetType() != y.GetType()) return false;

        // ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        // Justification: These can be null when the designer is in an invalid state. For example, if a NotFoundStep is used that no longer has the same outcomes.
        if (x.Source.Step?.Id == null || x.Target.Step?.Id == null || y.Source.Step?.Id == null || y.Target.Step?.Id == null)
            return false;
        // ReSharper restore ConditionalAccessQualifierIsNonNullableAccordingToAPIContract

        return x.Source.Step.Id.Equals(y.Source.Step.Id) && x.Target.Step.Id.Equals(y.Target.Step.Id) && x.Source.Port == y.Source.Port && x.Target.Port == y.Target.Port;
    }

    /// <inheritdoc />
    public int GetHashCode(Connection obj)
    {
        // ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        // Justification: These can be null when the designer is in an invalid state. For example, if it used a NotFoundStep that no longer has the same outcomes.
        return HashCode.Combine(obj.Source?.Step?.Id, obj.Target?.Step?.Id, obj.Source?.Port, obj.Target?.Port);
        // ReSharper restore ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
    }
}