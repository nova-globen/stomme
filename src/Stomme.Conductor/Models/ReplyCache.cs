using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stomme.Conductor.Models;

/// <summary>A model reply kept by the hash of its request (ADR 0004), with the figures the call cost when it was made.</summary>
public sealed record CachedReply(
    string Hash,
    string Kind,
    string Provider,
    string Model,
    string? AnsweredBy,
    DateTimeOffset At,
    string RequestSummary,
    string ResponseText,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    decimal Usd);

/// <summary>
/// Every model reply, reader's and judge's, cached in the output folder by a hash of the request (ADR 0004),
/// so a re-run after a fix does not re-spend and the same input yields the same bundle (ADR 0002).
/// </summary>
public sealed class ReplyCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ReplyCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    /// <summary>Called with every reply put, so the run state can index the cache (ADR 0017).</summary>
    public Action<CachedReply>? Indexed { get; set; }

    public static string HashOf(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public bool TryGet(string hash, [NotNullWhen(true)] out CachedReply? reply)
    {
        var path = PathOf(hash);
        reply = null;
        if (!File.Exists(path))
        {
            return false;
        }

        // Another call may be writing the same entry this moment (two identical pieces judged in parallel); a read
        // refused on the share is retried briefly, then counts as a miss, which only costs the call again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                reply = JsonSerializer.Deserialize<CachedReply>(File.ReadAllText(path), JsonOptions);
                return reply is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void Put(CachedReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var path = PathOf(reply.Hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written whole beside its place and moved over it, so a reader never sees a half-written entry and two
        // writers of the same entry both end with a whole one.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(reply, JsonOptions), new UTF8Encoding(false));
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
        {
            // The other writer won, with the same reply; on Windows a reader holding the entry refuses the move as access denied.
            File.Delete(temp);
        }

        Indexed?.Invoke(reply);
    }

    private string PathOf(string hash) => Path.Combine(Root, hash[..2], hash + ".json");
}
