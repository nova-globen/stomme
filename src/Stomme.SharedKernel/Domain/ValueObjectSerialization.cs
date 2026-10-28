namespace Stomme.SharedKernel.Domain;

// Markers that let infrastructure serializers round-trip value objects whose private constructors the
// reflection-based (de)serializers cannot use. A persistence layer reads the exposed value to write and a
// public static factory to reconstruct, so a value object opts into transparent persistence by implementing
// one of these and exposing the matching `Create` factory — no per-type converter and no coupling from the
// generic store to any module.

/// <summary>
/// A value object that round-trips as a single JSON string: serializers write <see cref="Value"/> and
/// reconstruct via a public <c>static Create(string)</c> factory.
/// </summary>
public interface IStringValueObject
{
    string Value { get; }
}

/// <summary>
/// A value object that round-trips as a JSON object of string→string: serializers write <see cref="Values"/>
/// and reconstruct via a public <c>static Create(IReadOnlyDictionary&lt;string, string&gt;)</c> factory.
/// </summary>
public interface IMapValueObject
{
    IReadOnlyDictionary<string, string> Values { get; }
}
