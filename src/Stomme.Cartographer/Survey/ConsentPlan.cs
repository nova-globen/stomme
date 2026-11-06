using System.Globalization;
using System.Text.Json.Nodes;
using Stomme.Cartographer.Sources;

namespace Stomme.Cartographer.Survey;

/// <summary>
/// What a run would send, and to whom, worked out without calling any provider (the owner's decision of 2026-11-05,
/// ADR 0028): the providers and their models, the files whose text may be sent with their sizes, the amount of
/// text, an estimate of the cost, and what stays on the machine (files that look like they hold a secret, files the
/// person excluded, backup copies, commit messages that look like they hold a secret). A consumer shows it for
/// consent before the run starts; the exclude and allow lists of the options change it.
/// </summary>
public static class ConsentPlan
{
    public const string FileName = "plan.json";

    /// <summary>
    /// US dollars per megabyte of document text, measured on seven first full runs of five repositories with the default models
    /// on 2026-11-04 and 2026-11-05 (docs/evidences): a range, since the cost follows how much of the text is ledgers and how often
    /// a text is read again, not only its size.
    /// </summary>
    private static readonly (double Low, double Expected, double High) ReaderRate = (0.01, 0.30, 0.80);
    private static readonly (double Low, double Expected, double High) JudgeRate = (0.004, 0.06, 0.12);

    public static JsonObject Build(SurveyOptions options, RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshot);
        var conductor = options.Conductor;
        var sent = snapshot.Files.Where(f => f.NotRead is null && f.IsText && f.Category is FileCategory.Document or FileCategory.TextUnknown).ToList();
        var bytes = sent.Sum(f => f.Size);
        var megabytes = bytes / 1_000_000.0;
        var commits = snapshot.Git?.Log() ?? [];
        var commitBytes = commits.Sum(c => (long)c.Subject.Length + c.Body.Length);
        var readerModels = new[] { conductor.Models.Default }.Concat(conductor.Models.ByPhase.Values).Distinct(StringComparer.Ordinal).ToList();
        var defaults = new[] { SurveyModels.Default.Default }.Concat(SurveyModels.Default.ByPhase.Values).ToHashSet(StringComparer.Ordinal);

        var files = new JsonArray();
        foreach (var file in sent)
        {
            files.Add(new JsonObject { ["path"] = file.Path, ["size"] = file.Size, ["category"] = file.Category.ToString(), ["tracked"] = file.Tracked });
        }

        var secrets = new JsonArray();
        var excluded = new JsonArray();
        var backups = new JsonArray();
        var allowed = new JsonArray();
        foreach (var file in snapshot.Files)
        {
            if (file.ExcludedBy is not null)
            {
                excluded.Add(new JsonObject { ["path"] = file.Path, ["pattern"] = file.ExcludedBy });
            }
            else if (file.Secret is not null && file.NotRead is not null)
            {
                secrets.Add(new JsonObject { ["path"] = file.Path, ["rule"] = file.Secret.Rule, ["line"] = file.Secret.Line });
            }
            else if (file.Secret is not null)
            {
                allowed.Add(new JsonObject { ["path"] = file.Path, ["rule"] = file.Secret.Rule, ["line"] = file.Secret.Line });
            }
            else if (file.NotRead is not null)
            {
                backups.Add(file.Path);
            }
        }

        var withheldCommits = new JsonArray();
        foreach (var (hash, rule) in snapshot.Git?.WithheldCommits.OrderBy(c => c.Key, StringComparer.Ordinal) ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            withheldCommits.Add(new JsonObject { ["commit"] = hash, ["rule"] = rule });
        }

        return new JsonObject
        {
            ["planVersion"] = 1,
            ["written"] = DateTimeOffset.Now,
            ["input"] = new JsonObject
            {
                ["path"] = snapshot.Root,
                ["commit"] = snapshot.Git?.Commit,
                ["branch"] = snapshot.Git?.Branch,
                ["files"] = snapshot.Files.Count,
                ["listedBy"] = snapshot.ListedBy,
            },
            ["providers"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "reader",
                    ["endpoint"] = conductor.Reader.Endpoint,
                    ["host"] = HostOf(conductor.Reader.Endpoint),
                    ["models"] = new JsonArray(readerModels.Select(m => (JsonNode)m).ToArray()),
                    ["keyPresent"] = !string.IsNullOrEmpty(conductor.Reader.Key),
                    ["receives"] = "the names of the files, the text of the files listed here in whole or in part, and the commit messages",
                },
                new JsonObject
                {
                    ["role"] = "judge",
                    ["endpoint"] = conductor.Judge.Endpoint,
                    ["host"] = HostOf(conductor.Judge.Endpoint),
                    ["models"] = new JsonArray(conductor.Judge.Model),
                    ["keyPresent"] = !string.IsNullOrEmpty(conductor.Judge.Key),
                    ["receives"] = "excerpts of the same files and commit messages, and what the reader wrote about them",
                },
            },
            ["text"] = new JsonObject
            {
                ["files"] = sent.Count,
                ["bytes"] = bytes,
                ["estimatedTokens"] = bytes / 4,
                ["commitMessages"] = commits.Count,
                ["commitMessageBytes"] = commitBytes,
            },
            ["files"] = files,
            ["notSent"] = new JsonObject
            {
                ["secrets"] = secrets,
                ["excluded"] = excluded,
                ["backupCopies"] = backups,
                ["commits"] = withheldCommits,
                ["otherFiles"] = snapshot.Files.Count(f => f.NotRead is null) - sent.Count,
            },
            ["allowed"] = allowed,
            ["estimate"] = new JsonObject
            {
                ["readerUsd"] = Range(megabytes, ReaderRate),
                ["judgeUsd"] = Range(megabytes, JudgeRate),
                ["calibratedForTheseModels"] = readerModels.All(defaults.Contains),
                ["basis"] = "US dollars per megabyte of document text, measured on seven first runs of five repositories with the default models on 2026-11-04 and 2026-11-05; a range and not a quote, and a run stops and asks at its ceiling",
            },
            ["ceilings"] = new JsonObject
            {
                ["readerUsd"] = conductor.Spending.CeilingUsd,
                ["judgeUsd"] = conductor.Spending.JudgeCeilingUsd,
                ["scope"] = conductor.Spending.Scope.ToString(),
            },
            ["exclude"] = new JsonArray(options.ExcludeFiles.Select(p => (JsonNode)p).ToArray()),
            ["allow"] = new JsonArray(options.AllowFiles.Select(p => (JsonNode)p).ToArray()),
            ["how"] = "To leave a file or a folder out, name its path or a pattern (*, **, ?) in the exclude list. To send a file the scan withheld, name its exact path in the allow list. Files not listed under files are not sent in this version: code, data, images and binaries are catalogued by name only.",
        };
    }

    /// <summary>The plan in one line, for a console and the log.</summary>
    public static string Line(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var providers = plan["providers"]!.AsArray();
        string Models(int i) => string.Join(", ", providers[i]!["models"]!.AsArray().Select(m => m!.GetValue<string>()));
        string Usd(string role) => string.Create(CultureInfo.InvariantCulture, $"${plan["estimate"]![role]!["low"]!.GetValue<double>():0.00} to ${plan["estimate"]![role]!["high"]!.GetValue<double>():0.00}");
        var text = plan["text"]!;
        var notSent = plan["notSent"]!;
        return string.Create(CultureInfo.InvariantCulture,
            $"To send: {text["files"]!.GetValue<int>()} file(s), {text["bytes"]!.GetValue<long>() / 1_000_000.0:0.00} MB of text, and {text["commitMessages"]!.GetValue<int>()} commit message(s), to the reader at {providers[0]!["host"]!.GetValue<string>()} ({Models(0)}), with excerpts to the judge at {providers[1]!["host"]!.GetValue<string>()} ({Models(1)}). Estimated: {Usd("readerUsd")} for the reader, {Usd("judgeUsd")} for the judge. Kept on this machine: {notSent["secrets"]!.AsArray().Count} file(s) that look like they hold a secret, {notSent["excluded"]!.AsArray().Count} excluded by the person, {notSent["backupCopies"]!.AsArray().Count} backup copy(ies), {notSent["commits"]!.AsArray().Count} commit message(s).");
    }

    private static JsonObject Range(double megabytes, (double Low, double Expected, double High) rate) => new()
    {
        ["low"] = Math.Round(Math.Max(0.01, megabytes * rate.Low), 2),
        ["expected"] = Math.Round(Math.Max(0.01, megabytes * rate.Expected), 2),
        ["high"] = Math.Round(Math.Max(0.02, megabytes * rate.High), 2),
    };

    // RelativeOrAbsolute and IsAbsoluteUri, never UriKind.Absolute: on Linux that reads a bare path as file:///.
    private static string HostOf(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.RelativeOrAbsolute, out var uri) && uri.IsAbsoluteUri ? uri.Host : endpoint;
}
