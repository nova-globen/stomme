using Stomme.Infrastructure;
using Xunit;

namespace Stomme.Tests.Pairing.Notes
{
    public interface INotes
    {
    }

    /// <summary>Named like the last segment of its namespace: the old string replace turned the namespace into <c>Pairing.INotes</c>.</summary>
    public sealed class Notes : INotes
    {
    }

    public interface IUnimplemented
    {
    }

    /// <summary>Paired by the convention alone - the interface sits beside it and the class does not implement it.</summary>
    public sealed class Unimplemented
    {
    }

    public sealed class Alone
    {
    }

    /// <summary>The convention-registered <c>IService</c> pairing (BL-251, BL-239's A9).</summary>
    public sealed class ServicePairingTests
    {
        [Fact]
        public void A_class_named_like_its_namespace_pairs_with_the_interface_it_implements() =>
            Assert.Equal(typeof(INotes), ServicePairing.InterfaceFor(typeof(Notes)));

        [Fact]
        public void An_interface_beside_the_class_still_pairs_by_name() =>
            Assert.Equal(typeof(IUnimplemented), ServicePairing.InterfaceFor(typeof(Unimplemented)));

        [Fact]
        public void A_class_with_no_such_interface_pairs_with_nothing() =>
            Assert.Null(ServicePairing.InterfaceFor(typeof(Alone)));
    }
}
