using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Options;
using Stomme.Conductor.Models;
using Stomme.Conductor.Runner;

namespace Stomme.Conductor.Models;

/// <summary>A judgement with its figures.</summary>
public sealed record JudgeResult(int CallSeq, JudgeReply Reply, CallCost Cost, bool Cached);

/// <summary>
/// The judge service: one request at a time through the reply cache, priced and recorded like every other
/// call (ADR 0004, 0008). Failures propagate as <see cref="JudgeUnavailableException"/>; the caller decides
/// what happens without a judgement, which is never "yes".
/// </summary>
public sealed class Judge
{
    private readonly IJudgeModel _model;
    private readonly JudgeOptions _options;
    private readonly ReplyCache _cache;
    private readonly SpendBook _spend;
    private readonly DecisionLog _log;
    private readonly PriceTable _prices;
    private readonly SemaphoreSlim _gate;

    public Judge(IJudgeModel model, JudgeOptions options, ReplyCache cache, SpendBook spend, DecisionLog log, PriceTable prices)
    {
        _model = model;
        _options = options;
        _cache = cache;
        _spend = spend;
        _log = log;
        _prices = prices;
        _gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
    }

    public JudgeOptions Options => _options;

    /// <summary>Called before a paid call with its estimated cost; may throw to stop the run (the judge's hard limit, ADR 0024).</summary>
    public Func<decimal, CancellationToken, Task>? BeforeSpending { get; set; }

    public async Task<JudgeResult> AskAsync(string phase, string purpose, JsonNode state, JsonObject questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);
        var canonical = _model.Name + "\n" + _options.Model + "\n" + state.ToJsonString() + "\n" + questions.ToJsonString();
        var hash = ReplyCache.HashOf(canonical);

        if (_cache.TryGet(hash, out var cached))
        {
            var reply = TypeSafeJudgeModel.Parse(cached.ResponseText, questions);
            var cost = new CallCost(cached.InputTokens, 0, cached.OutputTokens, 0, 0m, false);
            var call = _log.Record(new DecisionRecord
            {
                Phase = phase,
                Kind = "call",
                Subject = purpose,
                Decision = "judged from the cache",
                By = DecidedBy.Judge,
                Model = cached.AnsweredBy ?? cached.Model,
                Cost = cost,
                Cached = true,
            });
            return new JudgeResult(call.Seq, reply, cost, true);
        }

        var estimate = (_prices.Find(_options.Model)?.CostOf(TokenEstimate.Of(canonical), 0, 0) ?? 0m);
        if (BeforeSpending is not null)
        {
            await BeforeSpending(estimate, cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reply = await _model.AskAsync(state, questions, cancellationToken).ConfigureAwait(false);
            var price = _prices.Find(reply.Model) ?? _prices.Find(_options.Model);
            var cost = new CallCost(reply.InputTokens, 0, reply.OutputTokens, 0,
                price?.CostOf(reply.InputTokens, 0, reply.OutputTokens) ?? 0m, price is null);
            _spend.Add(SpendKind.Judge, cost);
            var call = _log.Record(new DecisionRecord
            {
                Phase = phase,
                Kind = "call",
                Subject = purpose,
                Decision = "judged",
                By = DecidedBy.Judge,
                Model = reply.Model,
                Cost = cost,
            });
            _cache.Put(new CachedReply(hash, "judge", _model.Name, _options.Model, reply.Model, DateTimeOffset.Now, purpose,
                Serialize(reply), reply.InputTokens, 0, reply.OutputTokens, 0, cost.Usd));
            return new JudgeResult(call.Seq, reply, cost, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The reply in the API's own shape, so the cache replays through the same parser.</summary>
    private static string Serialize(JudgeReply reply)
    {
        var answers = new JsonObject();
        foreach (var (id, answer) in reply.Answers)
        {
            answers[id] = answer switch
            {
                NoulAnswer n => new JsonObject { ["type"] = "noul", ["noul"] = n.Noul },
                ChoiceAnswer c => new JsonObject
                {
                    ["type"] = "choice",
                    ["choice"] = c.Choice,
                    ["confidence"] = c.Confidence,
                    ["probabilities"] = ToObject(c.Probabilities),
                },
                ScoreAnswer s => new JsonObject
                {
                    ["type"] = "score",
                    ["score"] = s.Score,
                    ["confidence"] = s.Confidence,
                    ["probabilities"] = ToObject(s.Probabilities),
                    ["legend"] = ToLegend(s.Legend),
                },
                _ => throw new InvalidOperationException("Unknown answer type."),
            };
        }

        return new JsonObject
        {
            ["model"] = reply.Model,
            ["answers"] = answers,
            ["usage"] = new JsonObject { ["input_tokens"] = reply.InputTokens, ["output_tokens"] = reply.OutputTokens },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, double> values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values)
        {
            o[k] = v;
        }

        return o;
    }

    private static JsonObject ToLegend(IReadOnlyDictionary<string, string> values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values)
        {
            o[k] = v;
        }

        return o;
    }
}

/// <summary>The playbook's judge questions, cloned and filled per use so a caller may not mutate the playbook's copy.</summary>
public static class JudgeQuestions
{
    /// <summary>A deep copy of a question set with every {{placeholder}} in its strings replaced.</summary>
    public static JsonObject Fill(JsonObject set, IReadOnlyDictionary<string, string>? values = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var copy = (JsonObject)set.DeepClone();
        if (values is { Count: > 0 })
        {
            Replace(copy, values);
        }

        return copy;
    }

    private static void Replace(JsonNode? node, IReadOnlyDictionary<string, string> values)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        o[key] = Fill(s, values);
                    }
                    else
                    {
                        Replace(o[key], values);
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        a[i] = Fill(s, values);
                    }
                    else
                    {
                        Replace(a[i], values);
                    }
                }

                break;
        }
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (k, v) in values)
        {
            text = text.Replace("{{" + k + "}}", v, StringComparison.Ordinal);
        }

        return text;
    }
}
