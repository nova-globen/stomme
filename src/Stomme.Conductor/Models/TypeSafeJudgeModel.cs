using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Conductor.Options;

namespace Stomme.Conductor.Models;

/// <summary>
/// <see cref="IJudgeModel"/> over TypeSafe's System One API (the Jev model), modelled on Fravashi's broker
/// adapter (the using-typesafe skill): bearer key, one retry on 429 and 529, strict parsing, and every
/// failure an exception rather than an answer. The key is added here and only here, and never appears in
/// an exception, a log line, or anything returned to a caller.
/// </summary>
public sealed class TypeSafeJudgeModel : IJudgeModel
{
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly JudgeOptions _options;

    public TypeSafeJudgeModel(HttpClient http, string key, JudgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _key = key;
        _options = options;
    }

    public string Name => "typesafe";

    public async Task<JudgeReply> AskAsync(JsonNode state, JsonObject questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);

        // RelativeOrAbsolute and IsAbsoluteUri, never UriKind.Absolute: on Linux that takes a bare path for an absolute URI.
        if (!Uri.TryCreate(_options.Endpoint, UriKind.RelativeOrAbsolute, out var endpoint)
            || !endpoint.IsAbsoluteUri
            || !(endpoint.Scheme == Uri.UriSchemeHttps
                || (_options.AllowInsecureEndpoint && endpoint.Scheme == Uri.UriSchemeHttp)))
        {
            throw new JudgeUnavailableException("insecure_endpoint");
        }

        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["state"] = state.DeepClone(),
            ["questions"] = questions.DeepClone(),
        }.ToJsonString();

        using var deadline = new CancellationTokenSource(_options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if ((status == 429 || status == 529) && attempt == 1)
                {
                    await Task.Delay(_options.RetryDelay, linked.Token).ConfigureAwait(false);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new JudgeUnavailableException("http_" + status + Detail(body));
                }

                return Parse(body, questions);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JudgeUnavailableException("timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new JudgeUnavailableException("unreachable", ex);
        }
    }

    /// <summary>A 400 or 422 names the malformed question; that detail is secret-free and worth keeping, cut short.</summary>
    private static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        var detail = body.Replace('\n', ' ').Replace('\r', ' ');
        return ": " + (detail.Length > 300 ? detail[..300] + "..." : detail);
    }

    /// <summary>Reads an answer body. Internal so the shape is tested without a network.</summary>
    internal static JudgeReply Parse(string body, JsonObject questions)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new JudgeUnavailableException("unreadable_answer", ex);
        }

        if (root?["answers"] is not JsonObject answers)
        {
            throw new JudgeUnavailableException("unreadable_answer");
        }

        var model = root["model"]?.GetValue<string>() ?? "";
        var usage = root["usage"] as JsonObject;
        var inputTokens = usage?["input_tokens"]?.GetValue<long>() ?? 0;
        var outputTokens = usage?["output_tokens"]?.GetValue<long>() ?? 0;

        var read = new Dictionary<string, JudgeAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in questions)
        {
            if (answers[id] is not JsonObject answer)
            {
                throw new JudgeUnavailableException("answer_missing_" + id);
            }

            var type = (question as JsonObject)?["type"]?.GetValue<string>() ?? answer["type"]?.GetValue<string>() ?? "noul";
            read[id] = type switch
            {
                "noul" => ReadNoul(id, answer),
                "choice" => ReadChoice(id, answer),
                "score" => ReadScore(id, answer),
                _ => throw new JudgeUnavailableException("answer_type_unknown_" + id),
            };
        }

        return new JudgeReply(model, read, inputTokens, outputTokens);
    }

    private static NoulAnswer ReadNoul(string id, JsonObject answer)
    {
        if (answer["noul"] is not JsonValue value || !value.TryGetValue<double>(out var noul) || !double.IsFinite(noul))
        {
            throw new JudgeUnavailableException("answer_unreadable_" + id);
        }

        return new NoulAnswer(noul);
    }

    private static ChoiceAnswer ReadChoice(string id, JsonObject answer)
    {
        var choice = answer["choice"]?.GetValue<string>() ?? throw new JudgeUnavailableException("answer_unreadable_" + id);
        return new ChoiceAnswer(choice, ReadConfidence(answer), ReadProbabilities(id, answer));
    }

    private static ScoreAnswer ReadScore(string id, JsonObject answer)
    {
        if (answer["score"] is not JsonValue value || !value.TryGetValue<double>(out var score) || !double.IsFinite(score))
        {
            throw new JudgeUnavailableException("answer_unreadable_" + id);
        }

        var legend = new Dictionary<string, string>(StringComparer.Ordinal);
        if (answer["legend"] is JsonObject legendObject)
        {
            foreach (var (level, text) in legendObject)
            {
                legend[level] = text?.GetValue<string>() ?? "";
            }
        }

        return new ScoreAnswer(score, ReadConfidence(answer), ReadProbabilities(id, answer), legend);
    }

    private static double? ReadConfidence(JsonObject answer) =>
        answer["confidence"] is JsonValue c && c.TryGetValue<double>(out var confidence) ? confidence : null;

    private static Dictionary<string, double> ReadProbabilities(string id, JsonObject answer)
    {
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        if (answer["probabilities"] is JsonObject p)
        {
            foreach (var (option, value) in p)
            {
                if (value is not JsonValue v || !v.TryGetValue<double>(out var probability))
                {
                    throw new JudgeUnavailableException("answer_unreadable_" + id);
                }

                probabilities[option] = probability;
            }
        }

        return probabilities;
    }
}
