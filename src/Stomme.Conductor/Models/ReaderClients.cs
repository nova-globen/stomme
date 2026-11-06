using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;
using Stomme.Conductor.Options;

namespace Stomme.Conductor.Models;

/// <summary>
/// Builds the reader's chat clients (ADR 0004): the OpenAI SDK against an OpenAI-compatible endpoint (MiMo's /v1),
/// one client per model, made once. The key is passed to the SDK and kept nowhere else.
/// </summary>
public sealed class ReaderClients
{
    private readonly Dictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ReaderOptions _options;
    private readonly string _key;

    public ReaderClients(ReaderOptions options, string key)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _options = options;
        _key = key;
    }

    public string Provider => _options.Protocol switch
    {
        ReaderProtocol.OpenAiCompatible => "openai-compatible:" + new Uri(_options.Endpoint).Host,
        ReaderProtocol.AnthropicCompatible => "anthropic-compatible:" + new Uri(_options.Endpoint).Host,
        _ => "unknown",
    };

    public IChatClient For(string model, ThinkingMode thinking)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var key = model + "|" + thinking;
        lock (_gate)
        {
            if (!_clients.TryGetValue(key, out var client))
            {
                client = Create(model, thinking);
                _clients[key] = client;
            }

            return client;
        }
    }

    private IChatClient Create(string model, ThinkingMode thinking)
    {
        switch (_options.Protocol)
        {
            case ReaderProtocol.OpenAiCompatible:
                var clientOptions = new OpenAIClientOptions
                {
                    Endpoint = new Uri(_options.Endpoint),
                    NetworkTimeout = _options.Timeout,
                };
                if (thinking != ThinkingMode.Default)
                {
                    clientOptions.AddPolicy(new ThinkingPolicy(thinking), PipelinePosition.PerCall);
                }

                var openAi = new OpenAIClient(new ApiKeyCredential(_key), clientOptions);
                return openAi.GetChatClient(model).AsIChatClient();
            case ReaderProtocol.AnthropicCompatible:
                throw new NotSupportedException("The Anthropic-compatible adapter is step 6 of the plan and is not built yet.");
            default:
                throw new NotSupportedException($"Unknown reader protocol {_options.Protocol}.");
        }
    }
}

/// <summary>
/// Turns the model's chain of thought on or off per request where the SDK has no field for it: MiMo reads a
/// top-level "thinking": {"type": "enabled" | "disabled"} in the request body (its API reference of 2026-10-20),
/// so the policy rewrites the JSON on its way out. With the thinking on, MiMo sets temperature and top_p itself.
/// </summary>
public sealed class ThinkingPolicy(ThinkingMode mode) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    /// <summary>The body with the thinking field set, or null when the mode is the default or the body is not a JSON object.</summary>
    public static string? Rewrite(string json, ThinkingMode mode)
    {
        if (mode == ThinkingMode.Default || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject body)
        {
            return null;
        }

        body["thinking"] = new JsonObject { ["type"] = mode == ThinkingMode.Off ? "disabled" : "enabled" };
        return body.ToJsonString();
    }

    private void Rewrite(PipelineMessage message)
    {
        if (message.Request.Content is null)
        {
            return;
        }

        using var buffer = new MemoryStream();
        message.Request.Content.WriteTo(buffer);
        var rewritten = Rewrite(Encoding.UTF8.GetString(buffer.ToArray()), mode);
        if (rewritten is not null)
        {
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
        }
    }
}
