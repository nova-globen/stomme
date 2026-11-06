using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Stomme.Conductor.Keys;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;

namespace Stomme.Cartographer.Cli;

/// <summary>The NAMES of the environment variables the console reads the keys from (ADR 0004, 0022); the values never leave the options.</summary>
internal static class Keys
{
    public const string Reader = "NG_FRAVASHI_MIMO_IMPORT";
    public const string Judge = "NG_FRAVASHI_JEV_IMPORT";
}

/// <summary>Finds the playbook folder when none is given: the working folder, then up from the executable; null means the embedded playbook (ADR 0022).</summary>
internal static class Locator
{
    public static string? FindPlaybook(string? given)
    {
        if (!string.IsNullOrWhiteSpace(given))
        {
            return Path.GetFullPath(given);
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var folder = new DirectoryInfo(start);
            while (folder is not null)
            {
                var candidate = Path.Combine(folder.FullName, "playbooks", "survey-repository", "playbook.json");
                if (File.Exists(candidate))
                {
                    return Path.GetDirectoryName(candidate)!;
                }

                folder = folder.Parent;
            }
        }

        return null;
    }
}

/// <summary>The console's prompt for the interactive question policy (ADR 0007): options, free text, or empty to keep the default.</summary>
internal static class ConsoleQuestions
{
    public static Task<string?> AskAsync(Question question, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Question ({question.Phase}, key {question.Key}):"));
        Console.WriteLine(question.Text);
        if (!string.IsNullOrWhiteSpace(question.Context))
        {
            Console.WriteLine("Context: " + question.Context.Trim());
        }

        foreach (var option in question.Options)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {option.Value}: {option.Meaning}"));
        }

        Console.WriteLine("Default if left empty: " + question.WithOptionDefault().DefaultIfUnanswered);
        Console.Write("> ");
        var answer = Console.ReadLine();
        return Task.FromResult(string.IsNullOrWhiteSpace(answer) ? null : answer.Trim());
    }
}

/// <summary>
/// The tiny live call to each API (the plan, step 1): a reader call that asks for a two-field JSON object and a
/// judge call with one yes/no question about a one-line sentence. Each costs well under a cent. Prints the
/// figures (tokens, cost, model version, time) and never a key.
/// </summary>
internal static class Probe
{
    public static async Task<int> RunAsync(string outputFolder, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetFullPath(outputFolder), "probe");
        Directory.CreateDirectory(folder);
        var readerOptions = new ReaderOptions();
        var judgeOptions = new JudgeOptions();
        var models = new PhaseModels();
        var prices = PriceTable.Default;
        var runId = "probe-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        using var log = new DecisionLog(folder, runId);
        var spend = new SpendBook(Path.Combine(folder, "spend.json"), runId, folder);
        var cache = new ReplyCache(Path.Combine(folder, "cache"));
        var exit = 0;

        var (readerKey, readerSource) = KeyReader.Read(Keys.Reader);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Reader key {Keys.Reader}: {readerSource}, {(readerKey?.Length ?? 0)} characters."));
        if (readerKey is null)
        {
            exit = 1;
        }
        else
        {
            try
            {
                var clients = new ReaderClients(readerOptions, readerKey);
                var reader = new Reader(clients.For, clients.Provider, readerOptions, cache, spend, log, prices);
                var watch = Stopwatch.StartNew();
                var reply = await reader.AskAsync(new ReaderRequest(
                    "probe", "probe: a two-field JSON object", models.Default,
                    "You answer with one JSON object and nothing else.",
                    [new ReaderPart.Instruction("Reply with exactly this JSON object, filling in your model name: {\"ok\": true, \"model\": \"<your model name>\"}")])
                {
                    MaxOutputTokens = 2_000,
                }, cancellationToken).ConfigureAwait(false);
                watch.Stop();
                var ok = reply.Json?["ok"]?.GetValue<bool>() == true;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Reader ({clients.Provider}, {models.Default}): {(ok ? "ok" : "unexpected reply")} in {watch.ElapsedMilliseconds} ms; answered by {reply.AnsweredBy ?? "(no model id)"}; {reply.Cost.InputTokens} input tokens ({reply.Cost.CachedInputTokens} cached), {reply.Cost.OutputTokens} output tokens ({reply.Cost.ReasoningTokens} reasoning); cost {PriceTable.Dollars(reply.Cost.Usd)}{(reply.Cached ? " (from the cache)" : "")}."));
                Console.WriteLine("Reader reply text: " + reply.Text.Replace('\n', ' ').Trim());
                if (!ok)
                {
                    exit = 2;
                }
            }
            catch (ReaderException ex)
            {
                Console.Error.WriteLine("Reader call failed: " + ex.Message);
                exit = 2;
            }
        }

        var (judgeKey, judgeSource) = KeyReader.Read(Keys.Judge);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Judge key {Keys.Judge}: {judgeSource}, {(judgeKey?.Length ?? 0)} characters."));
        if (judgeKey is null)
        {
            exit = Math.Max(exit, 1);
        }
        else
        {
            try
            {
                using var http = new HttpClient();
                var judge = new Judge(new TypeSafeJudgeModel(http, judgeKey, judgeOptions), judgeOptions, cache, spend, log, prices);
                var state = new JsonObject { ["text"] = "The build passed and every test is green; the release can go out today." };
                var questions = new JsonObject
                {
                    ["tests_passed"] = new JsonObject { ["type"] = "noul", ["instructions"] = "Does `text` say that the tests passed?" },
                    ["tests_failed"] = new JsonObject { ["type"] = "noul", ["instructions"] = "Does `text` say that a test failed?" },
                };
                var watch = Stopwatch.StartNew();
                var result = await judge.AskAsync("probe", "probe: two yes/no questions about one sentence", state, questions, cancellationToken).ConfigureAwait(false);
                watch.Stop();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Judge (typesafe, {judgeOptions.Model}): answered by {result.Reply.Model} in {watch.ElapsedMilliseconds} ms; tests_passed {result.Reply.Noul("tests_passed"):0.000}, tests_failed {result.Reply.Noul("tests_failed"):0.000}; {result.Cost.InputTokens} input tokens, {result.Cost.OutputTokens} output tokens; cost {PriceTable.Dollars(result.Cost.Usd)}{(result.Cached ? " (from the cache)" : "")}."));
            }
            catch (JudgeUnavailableException ex)
            {
                Console.Error.WriteLine("Judge call failed: " + ex.Message);
                exit = Math.Max(exit, 2);
            }
        }

        log.RenderMarkdown();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Spent {PriceTable.Dollars(spend.RunUsd)} on this probe (reader {PriceTable.Dollars(spend.RunUsdOf(SpendKind.Reader))}, judge {PriceTable.Dollars(spend.RunUsdOf(SpendKind.Judge))}); the log is {log.MarkdownPath}."));
        return exit;
    }
}
