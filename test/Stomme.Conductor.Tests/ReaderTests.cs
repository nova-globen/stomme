using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Stomme.Conductor.Ledger;
using Stomme.Conductor.Models;
using Stomme.Conductor.Options;

namespace Stomme.Conductor.Tests;

/// <summary>A reader that answers after a delay, and may fail a number of times first, to prove the log never goes silent on a slow or flaky call.</summary>
internal sealed class SlowChatClient(TimeSpan delay, int failures) : IChatClient
{
    private int _failures = failures;

    public int Calls { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        await Task.Delay(delay, cancellationToken);
        if (_failures > 0)
        {
            _failures--;
            throw new HttpRequestException("the scripted transport failed on purpose");
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"ok\": true}"))
        {
            ModelId = "slow-1",
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 },
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A reader that answers at once with the usage figures it is given, to prove how the book prices them.</summary>
internal sealed class UsageChatClient(UsageDetails usage) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"ok\": true}")) { ModelId = "mimo-v2.6-flash", Usage = usage });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class ReaderTests
{
    private static (Reader Reader, DecisionLog Log) Setup(TempFolder temp, IChatClient client, ReaderOptions options)
    {
        var log = new DecisionLog(temp.Path, "run-1");
        var reader = new Reader((_, _) => client, "test", options, new ReplyCache(Path.Combine(temp.Path, "cache")), new SpendBook(Path.Combine(temp.Path, "spend.json"), "run-1", temp.Path), log, PriceTable.Default);
        return (reader, log);
    }

    private static ReaderRequest Request(string model = "mimo-v2.6-pro") =>
        new("probe", "a slow question", model, "Answer with one JSON object.", [new ReaderPart.Instruction("Say ok.")]);

    [Fact]
    public async Task TheThinkingModeIsPartOfTheCacheKey()
    {
        using var temp = new TempFolder();
        var client = new SlowChatClient(TimeSpan.FromMilliseconds(1), failures: 0);
        var (reader, log) = Setup(temp, client, new ReaderOptions { WaitingNotice = TimeSpan.Zero, Retries = 0 });

        await reader.AskAsync(Request() with { Thinking = ThinkingMode.Off }, CancellationToken.None);
        await reader.AskAsync(Request() with { Thinking = ThinkingMode.On }, CancellationToken.None);
        var again = await reader.AskAsync(Request() with { Thinking = ThinkingMode.Off }, CancellationToken.None);

        // The same prompt with another chain of thought is another call; the same mode again is answered from the cache.
        Assert.Equal(2, client.Calls);
        Assert.True(again.Cached);
        log.Dispose();
    }

    [Fact]
    public async Task CachedAndReasoningTokensArePricedFromTheAdapterProperties()
    {
        using var temp = new TempFolder();
        var usage = new UsageDetails { InputTokenCount = 10_000, CachedInputTokenCount = 8_000, OutputTokenCount = 1_000, ReasoningTokenCount = 600 };
        var (reader, log) = Setup(temp, new UsageChatClient(usage), new ReaderOptions { WaitingNotice = TimeSpan.Zero, Retries = 0 });

        var reply = await reader.AskAsync(Request("mimo-v2.6-flash"), CancellationToken.None);

        // 2,000 uncached at $0.14, 8,000 cached at $0.0028, 1,000 output at $0.28, per million.
        Assert.Equal(8_000, reply.Cost.CachedInputTokens);
        Assert.Equal(600, reply.Cost.ReasoningTokens);
        Assert.Equal(0.0005824m, reply.Cost.Usd);
        Assert.False(reply.Cost.Estimated);
        log.Dispose();
    }

    [Fact]
    public async Task CachedAndReasoningTokensAreStillReadFromAdditionalCounts()
    {
        using var temp = new TempFolder();
        var usage = new UsageDetails
        {
            InputTokenCount = 10_000,
            OutputTokenCount = 1_000,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["InputTokenDetails.CachedTokenCount"] = 8_000, ["OutputTokenDetails.ReasoningTokenCount"] = 600 },
        };
        var (reader, log) = Setup(temp, new UsageChatClient(usage), new ReaderOptions { WaitingNotice = TimeSpan.Zero, Retries = 0 });

        var reply = await reader.AskAsync(Request("mimo-v2.6-flash"), CancellationToken.None);

        Assert.Equal(8_000, reply.Cost.CachedInputTokens);
        Assert.Equal(600, reply.Cost.ReasoningTokens);
        Assert.Equal(0.0005824m, reply.Cost.Usd);
        log.Dispose();
    }

    [Fact]
    public async Task ASlowCallIsNotedInTheLogWhileItIsInFlight()
    {
        using var temp = new TempFolder();
        var notices = new List<TimeSpan>();
        var (reader, log) = Setup(temp, new SlowChatClient(TimeSpan.FromMilliseconds(900), failures: 0),
            new ReaderOptions { WaitingNotice = TimeSpan.FromMilliseconds(150), Retries = 0 });
        reader.Waiting = (_, elapsed) => notices.Add(elapsed);

        var reply = await reader.AskAsync(Request(), CancellationToken.None);
        Assert.True(reply.Json!["ok"]!.GetValue<bool>());
        Assert.NotEmpty(notices);
        var waiting = log.Records.Where(r => r.Kind == "call-waiting").ToList();
        Assert.NotEmpty(waiting);
        Assert.Contains("still waiting on the reader", waiting[0].Decision, StringComparison.Ordinal);
        Assert.Equal("a slow question", waiting[0].Subject);
        log.Dispose();
    }

    [Fact]
    public async Task ARetryIsLoggedAndTheAnswerStillComes()
    {
        using var temp = new TempFolder();
        var client = new SlowChatClient(TimeSpan.FromMilliseconds(10), failures: 1);
        var (reader, log) = Setup(temp, client, new ReaderOptions { WaitingNotice = TimeSpan.Zero, Retries = 1, RetryDelay = TimeSpan.FromMilliseconds(20) });

        var reply = await reader.AskAsync(Request(), CancellationToken.None);
        Assert.True(reply.Json!["ok"]!.GetValue<bool>());
        Assert.Equal(2, client.Calls);
        var retry = Assert.Single(log.Records, r => r.Kind == "call-retry");
        Assert.Contains("attempt 1 of 2", retry.Decision, StringComparison.Ordinal);
        Assert.Contains("scripted transport failed", retry.Decision, StringComparison.Ordinal);

        // The lost attempt is booked at an estimate of its prompt, so the book never undercounts a cut the provider billed.
        Assert.Contains("booked at an estimate", retry.Decision, StringComparison.Ordinal);
        Assert.Contains("estimated", retry.Flags);
        Assert.True(retry.Cost!.Estimated);
        Assert.True(retry.Cost.InputTokens > 0);
        Assert.True(retry.Cost.Usd > 0m);
        log.Dispose();
    }

    [Fact]
    public async Task ACallThatFailsEveryAttemptIsRecordedAsFailed()
    {
        using var temp = new TempFolder();
        var client = new SlowChatClient(TimeSpan.FromMilliseconds(10), failures: 3);
        var (reader, log) = Setup(temp, client, new ReaderOptions { WaitingNotice = TimeSpan.Zero, Retries = 1, RetryDelay = TimeSpan.FromMilliseconds(20) });

        await Assert.ThrowsAsync<ReaderException>(() => reader.AskAsync(Request(), CancellationToken.None));
        Assert.Equal(2, client.Calls);
        var failed = Assert.Single(log.Records, r => r.Kind == "call" && r.Flags.Contains("failed"));
        Assert.Contains("scripted transport failed", failed.Decision, StringComparison.Ordinal);

        // Both attempts were lost, and both are booked: one as the retry, the last as a lost call.
        Assert.Single(log.Records, r => r.Kind == "call-retry");
        var lost = Assert.Single(log.Records, r => r.Kind == "call-lost");
        Assert.Contains("attempt 2 of 2", lost.Decision, StringComparison.Ordinal);
        Assert.True(lost.Cost!.Estimated);
        log.Dispose();
    }
}
