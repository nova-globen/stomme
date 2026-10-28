using System.Reflection;

namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Smart enum pattern for domain enumerations with behavior.
/// Use instead of bare enums when the enumeration has associated logic.
/// </summary>
public abstract class Enumeration<TEnum> : IEquatable<Enumeration<TEnum>>
    where TEnum : Enumeration<TEnum>
{
    private static readonly Lazy<Dictionary<int, TEnum>> _enumerations = new(
        () => GetAllEnumerations().ToDictionary(e => e.Value));

    protected Enumeration(int value, string name)
    {
        Value = value;
        Name = name;
    }

    public int Value { get; }
    public string Name { get; }

    public static TEnum? FromValue(int value) =>
        _enumerations.Value.GetValueOrDefault(value);

    public static TEnum? FromName(string name) =>
        _enumerations.Value.Values.SingleOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyCollection<TEnum> GetAll() =>
        _enumerations.Value.Values.ToList().AsReadOnly();

    public bool Equals(Enumeration<TEnum>? other) =>
        other is not null && Value == other.Value;

    public override bool Equals(object? obj) =>
        obj is Enumeration<TEnum> other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Name;

    private static IEnumerable<TEnum> GetAllEnumerations()
    {
        var enumerationType = typeof(TEnum);
        return enumerationType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => enumerationType.IsAssignableFrom(f.FieldType))
            .Select(f => (TEnum)f.GetValue(null)!);
    }
}
