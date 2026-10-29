using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// n8n's palette has a node per service (Postgres, ServiceNow, a REST API); here a service is reached through one of
    /// the site's connections, which holds its credentials. So a query naming a connection - or its connector ("postgres") -
    /// offers that connection with each step that can use it, the step added with it chosen; and a step's operation is chosen
    /// among its connection's connector's own, with what each takes said.
    /// </summary>
    public static class ConnectionChoices
    {
        /// <summary>The operation a connector answers Send through connection with (the REST connector's).</summary>
        public const string SendOperation = "send";

        /// <summary>The operation a chat connector answers Send chat message with (Slack's, Telegram's, Discord's).</summary>
        public const string MessageOperation = "message";

        /// <summary>At most this many connections are offered for one query, after the steps.</summary>
        public const int MaxOffers = 8;

        /// <summary>A connection offered with a step that uses it.</summary>
        public sealed record Offer(StepCatalogEntry Entry, ConnectionView Connection);

        /// <summary>
        /// The site's connections whose name, connector or connector type holds every word of <paramref name="query"/>, each with
        /// the steps that suit its connector: a step choosing an operation (<see cref="PipelineInputHints.ConnectorOperation"/>)
        /// one whose connector reads - has an operation other than send - and a step with no operation of its own one whose
        /// connector sends. With the connectors unknown (<paramref name="connectors"/> null), every connection step suits.
        /// </summary>
        public static List<Offer> Matching(IEnumerable<StepCatalogEntry> catalog, IEnumerable<ConnectionView> connections, IReadOnlyCollection<ConnectorView> connectors, string query)
        {
            var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var steps = (catalog ?? Enumerable.Empty<StepCatalogEntry>()).Where(entry => ConnectionInput(entry) is not null).ToList();
            if (words.Length == 0 || steps.Count == 0)
            {
                return new List<Offer>();
            }

            return (connections ?? Enumerable.Empty<ConnectionView>())
                .Where(connection => connection is not null && words.All(word => Holds(connection, word)))
                .SelectMany(connection => steps.Where(entry => Suits(entry, connection, ConnectorOf(connection, connectors), connectors is not null)).Select(entry => new Offer(entry, connection)))
                .Take(MaxOffers)
                .ToList();
        }

        /// <summary>The step's input that names a connection, or null.</summary>
        public static string ConnectionInput(StepCatalogEntry entry) =>
            entry?.Inputs?.FirstOrDefault(input => input.UIHint == PipelineInputHints.ConnectionPicker)?.Name;

        /// <summary>The connector of the connection a step names, when both are known.</summary>
        public static ConnectorView ConnectorOf(string connectionId, IEnumerable<ConnectionView> connections, IReadOnlyCollection<ConnectorView> connectors)
        {
            var connection = (connections ?? Enumerable.Empty<ConnectionView>()).FirstOrDefault(known => string.Equals(known.Id.ToString(), connectionId?.Trim(), StringComparison.OrdinalIgnoreCase));
            return connection is null ? null : ConnectorOf(connection, connectors);
        }

        /// <summary>The operations a step chooses among: the connector's own but send and message, which steps of their own do.</summary>
        public static List<ConnectorOperationView> Operations(ConnectorView connector) =>
            (connector?.Operations ?? Array.Empty<ConnectorOperationView>())
                .Where(operation => !string.Equals(operation.Name, SendOperation, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(operation.Name, MessageOperation, StringComparison.OrdinalIgnoreCase))
                .ToList();

        /// <summary>Whether a connection picker takes a connection of this connector type: any, unless it names the types it can use.</summary>
        public static bool Accepts(StepInputView input, string connectorType) =>
            input?.ConnectorTypes is not { Count: > 0 } types || types.Contains(connectorType ?? "", StringComparer.OrdinalIgnoreCase);

        /// <summary>The site's connections a picker offers.</summary>
        public static List<ConnectionView> Offered(StepInputView input, IEnumerable<ConnectionView> connections) =>
            (connections ?? Enumerable.Empty<ConnectionView>()).Where(connection => connection is not null && Accepts(input, connection.ConnectorType)).ToList();

        /// <summary>The connectors a connection made from the picker can be.</summary>
        public static List<ConnectorView> Makeable(StepInputView input, IEnumerable<ConnectorView> connectors) =>
            (connectors ?? Enumerable.Empty<ConnectorView>()).Where(connector => connector is not null && Accepts(input, connector.ConnectorType)).ToList();

        /// <summary>What a picker with nothing to offer says: the kinds it wants by their names, when it names any.</summary>
        public static string NoneYet(StepInputView input, IReadOnlyCollection<ConnectorView> connectors)
        {
            if (input?.ConnectorTypes is not { Count: > 0 } types)
            {
                return "This site has no connections yet - make one here, or under Admin, Connections.";
            }

            var names = types.Select(type => connectors?.FirstOrDefault(connector => string.Equals(connector.ConnectorType, type, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? type).ToList();
            var kinds = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];
            return $"This site has no {kinds} connection yet - make one here, or under Admin, Connections.";
        }

        private static ConnectorView ConnectorOf(ConnectionView connection, IReadOnlyCollection<ConnectorView> connectors) =>
            connectors?.FirstOrDefault(connector => string.Equals(connector.ConnectorType, connection.ConnectorType, StringComparison.OrdinalIgnoreCase));

        private static bool Holds(ConnectionView connection, string word) =>
            (connection.Name ?? "").Contains(word, StringComparison.CurrentCultureIgnoreCase)
            || (connection.ConnectorDisplayName ?? "").Contains(word, StringComparison.CurrentCultureIgnoreCase)
            || (connection.ConnectorType ?? "").Contains(word, StringComparison.OrdinalIgnoreCase);

        private static bool Suits(StepCatalogEntry entry, ConnectionView connection, ConnectorView connector, bool connectorsKnown)
        {
            if (entry.Inputs.FirstOrDefault(input => input.UIHint == PipelineInputHints.ConnectionPicker) is { ConnectorTypes.Count: > 0 } picker)
            {
                return Accepts(picker, connection.ConnectorType); // the step says which kinds it uses
            }

            if (!connectorsKnown)
            {
                return true;
            }

            if (connector is null)
            {
                return false; // its connector is not installed: no step can use it
            }

            var choosesOperation = entry.Inputs.Any(input => input.UIHint == PipelineInputHints.ConnectorOperation);
            return choosesOperation
                ? Operations(connector).Count > 0
                : connector.Operations.Any(operation => string.Equals(operation.Name, SendOperation, StringComparison.OrdinalIgnoreCase));
        }
    }
}
