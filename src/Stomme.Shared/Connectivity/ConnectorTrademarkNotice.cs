#nullable enable
namespace Stomme.Connectivity
{
    /// <summary>
    /// The global third-party trademark notice the connector catalogue carries (BL-96 increment 6, from the predecessor platform's icon and
    /// logo licensing review of 2026-07-12, kept in <c>docs/legal/connector-icon-licensing-review/</c>): a product name
    /// beside a connector identifies the external product the connector interoperates with and nothing more. The catalogue
    /// shows only brand-neutral monograms - no vendor logo is cleared for this surface - so the notice is the one piece of
    /// vendor text the page renders, and it is rendered on every catalogue.
    /// </summary>
    public static class ConnectorTrademarkNotice
    {
        public const string Text =
            "Nova Globen Stomme and Nova Globen are not affiliated with, sponsored by, endorsed by, or approved by the third-party vendors " +
            "identified here. Product names, service names, trademarks, and registered trademarks are the property of their " +
            "respective owners. References are used solely to identify the external products and services with which Stomme interoperates.";
    }
}
