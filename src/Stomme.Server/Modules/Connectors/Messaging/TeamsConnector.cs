#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Messaging
{
    /// <summary>
    /// Microsoft Teams (BL-229, n8n's Microsoft Teams node, its channel and chat message create): Microsoft Graph
    /// (<c>https://graph.microsoft.com/v1.0</c>) as a person signed in - delegated, since Graph lets an app post to a channel or a
    /// chat only on someone's behalf - over the OAuth2 REFRESH-TOKEN scheme: an Entra app's client id and secret and a refresh token
    /// granted <c>ChannelMessage.Send</c> and <c>ChatMessage.Send</c>, exchanged at the tenant's token endpoint (the
    /// <c>token_url</c> setting) through the shared <see cref="OAuth2TokenProvider"/>. A test reads who the token is (<c>me</c>).
    /// </summary>
    /// <remarks>
    /// <c>message</c> - the chat connectors' one operation - sends <c>to</c> a channel, <c>{team id}/{channel id}</c>, or a chat,
    /// its id (<c>19:...</c>); <c>reply_to</c> a channel message's id answers it in its thread (a chat has no threads, so a reply
    /// there is refused). The team, the channel, the chat and the message are held to their shapes and escaped before they reach the
    /// path. The text goes as text. Teams' refusal - a channel not found, no permission - comes back as the record's
    /// <c>refused</c>, in Graph's words.
    /// </remarks>
    public sealed class TeamsConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://graph.microsoft.com/v1.0";

        public const string DefaultTokenUrl = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token";

        private static readonly Regex TeamId = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        private static readonly Regex ThreadId = new("^19:[A-Za-z0-9_.@-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex MessageId = new("^[0-9]{1,30}$", RegexOptions.CultureInvariant);

        /// <summary>What a refresh asks Microsoft's v2 endpoint for: every Graph permission the app was granted, and a refresh token again.</summary>
        public const string Scope = "https://graph.microsoft.com/.default offline_access";

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "teams";

        public string DisplayName => "Microsoft Teams";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        /// <summary>The refresh-token scheme as Entra names its parts.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Application (client) ID", ["client-secret"] = "Client secret", ["refresh-token"] = "Refresh token (ChannelMessage.Send, ChatMessage.Send)",
        };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://www.microsoft.com/microsoft-teams");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/graph/api/channel-post-messages");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "The tenant's token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token.") { Default = DefaultTokenUrl },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            ChatMessages.Describe("A channel - its team's id/the channel's id (19:...@thread.tacv2) - or a chat's id (19:...).", "A channel message's id, to answer it in its thread."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, api + "/me?$select=displayName,userPrincipalName");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var (status, json) = await ChatMessages.ReadAsync(client, request, cancellationToken);
                return status is >= 200 and < 300
                    ? ConnectionTestResult.Ok($"Signed in as {ChatMessages.Text(json?["displayName"]) ?? ChatMessages.Text(json?["userPrincipalName"]) ?? "the token's user"}.")
                    : ConnectionTestResult.Fail(json?["error"]?["message"] is { } reason ? "Microsoft Teams: " + ChatMessages.Text(reason) : $"HTTP {status}.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var message = ChatMessages.Read(context.Parameters, out var problem) ?? throw new InvalidOperationException(problem);
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);

            // Where it goes, before a token is asked for: a channel (team/channel) or a chat, a reply only in a channel's thread.
            string path;
            var parts = message.To.Split('/');
            if (parts.Length == 2)
            {
                if (!TeamId.IsMatch(parts[0]) || !ThreadId.IsMatch(parts[1]))
                {
                    yield return ChatMessages.Refused(DisplayName, $"'{message.To}' is not a channel - its team's id/the channel's id (19:...).");
                    yield break;
                }

                path = $"{api}/teams/{parts[0]}/channels/{Uri.EscapeDataString(parts[1])}/messages";
                if (message.ReplyTo is { } reply)
                {
                    if (!MessageId.IsMatch(reply))
                    {
                        yield return ChatMessages.Refused(DisplayName, $"'{reply}' is not a message's id (digits).");
                        yield break;
                    }

                    path += $"/{reply}/replies";
                }
            }
            else if (parts.Length == 1 && ThreadId.IsMatch(message.To))
            {
                if (message.ReplyTo is not null)
                {
                    yield return ChatMessages.Refused(DisplayName, "a chat has no threads to answer in - leave In reply to empty.");
                    yield break;
                }

                path = $"{api}/chats/{Uri.EscapeDataString(message.To)}/messages";
            }
            else
            {
                yield return ChatMessages.Refused(DisplayName, $"'{message.To}' is not a channel (its team's id/the channel's id) or a chat (19:...).");
                yield break;
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (status, json) = await ChatMessages.PostAsync(client, request, new JsonObject { ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = message.Text } }, cancellationToken);
            if (status is >= 200 and < 300 && ChatMessages.Text(json?["id"]) is { Length: > 0 } id)
            {
                yield return ChatMessages.Sent(id, message.To);
            }
            else if (status is >= 400 and < 500 && json?["error"]?["message"] is { } reason)
            {
                yield return ChatMessages.Refused(DisplayName, ChatMessages.Text(reason));
            }
            else
            {
                throw new InvalidOperationException($"HTTP {status}: not Microsoft Graph's answer.");
            }
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Scope = Scope,
                Timeout = timeout,
            }, cancellationToken);
    }
}
