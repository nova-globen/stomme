using Stomme.SharedKernel.Domain;

namespace Stomme.Tenancy.Domain;

/// <summary>
/// Globally unique, immutable identity of a tenant. Every tenant-owned aggregate elsewhere
/// carries a <see cref="TenantId"/> resolved from the ambient tenant context.
/// </summary>
public sealed class TenantId : ValueObject
{
    /// <summary>The well-known single-tenant identity used by on-prem / single-tenant deployments.</summary>
    public static readonly TenantId Default = new(new Guid("00000000-0000-0000-0000-000000000001"));

    private TenantId(Guid value) => Value = value;

    public Guid Value { get; }

    /// <summary>Creates a <see cref="TenantId"/> from a non-empty GUID.</summary>
    public static TenantId From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must be a non-empty GUID.", nameof(value));
        }

        return new TenantId(value);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}
