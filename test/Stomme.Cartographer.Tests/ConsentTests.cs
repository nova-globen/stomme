using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Stomme.Cartographer.Catalogue;
using Stomme.Cartographer.Sources;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;
using Stomme.Conductor.State;

namespace Stomme.Cartographer.Tests;

/// <summary>
/// The owner's decision of 2026-11-05 (ADR 0028): before anything leaves the machine, a file that looks like it
/// holds a secret is skipped and listed; a consumer gets what a consent screen needs before the run starts, and
/// a way to leave files out.
/// </summary>
public sealed class ConsentTests
{
    // Built at run time, in pieces: no line of this file has the shape of a credential.
    private static readonly string Token = "gh" + "p_" + "A1b2C3d4E5f6G7h8I9j0" + "K1l2M3n4O5p6Q7r8";
    private static readonly string KeyBlock = "-----BEGIN " + "RSA PRIVATE " + "KEY-----";

    [Fact]
    public void TheScanKnowsFilesOfCredentialsByNameAndSecretsByTheirShapeAndNeverAPlaceholder()
    {
        Assert.Equal("env-file", SecretScan.ByName(".env")!.Rule);
        Assert.Equal("env-file", SecretScan.ByName("deploy/.env.production")!.Rule);
        Assert.Null(SecretScan.ByName(".env.example"));
        Assert.Equal("key-file", SecretScan.ByName("certs/site.pfx")!.Rule);
        Assert.Equal("credential-file", SecretScan.ByName("home/.ssh/id_ed25519")!.Rule);
        Assert.Null(SecretScan.ByName("home/.ssh/id_ed25519.pub"));
        Assert.Null(SecretScan.ByName("docs/secrets-management.md"));
        Assert.Null(SecretScan.ByName("src/TokenService.cs"));

        var hit = SecretScan.InText("# Deploy\n\nUse this:\n\n    export GH=" + Token + "\n")!;
        Assert.Equal(("github-token", 5), (hit.Rule, hit.Line));
        Assert.DoesNotContain(Token, hit.ToString(), StringComparison.Ordinal);
        Assert.Equal("private-key-block", SecretScan.InText("x\n" + KeyBlock + "\nMIIE\n")!.Rule);
        Assert.Equal("password-in-url", SecretScan.InText("DATABASE_URL=postgres://app:" + "k9Zq2" + "Lm7Xw4@db.internal:5432/app")!.Rule);
        Assert.Equal("connection-string-password", SecretScan.InText("Server=db;Database=app;User Id=sa;Pass" + "word=" + "Tr0ub4dor" + "Xy9;")!.Rule);
        Assert.Equal("assigned-secret", SecretScan.InText("{ \"apiKey\": \"" + "q7Hd82LmZx" + "04PvT1sWb6" + "\" }")!.Rule);

        // What stands for a secret is none: a variable, an elision, an example, the name of where it lives.
        foreach (var harmless in new[]
        {
            "export GH=gh" + "p_" + new string('x', 36),
            "AWS_ACCESS_KEY_ID=AKIA" + "IOSFODNN7EXAMPLE",
            "DATABASE_URL=postgres://user:pass" + "word@localhost:5432/app",
            "Server=db;Database=app;User Id=sa;Pass" + "word=<your password>;",
            "{ \"apiKey\": \"${API_KEY_OF_THE_SERVICE}\" }",
            "tokenUrl: \"https://login.example.org/oauth2/v2.0/token\"",
            "secretName = \"prod-database-credentials-2024\"",
            "<assemblyIdentity name=\"System.Runtime\" publicKeyToken=\"b03f5f7f11d50a3a\" />",
            "The API key goes into the vault; never write a password into a file.",
        })
        {
            Assert.Null(SecretScan.InText(harmless));
        }
    }

    [Fact]
    public void AFileIsNamedByItsPathItsFolderOrAPattern()
    {
        Assert.True(PathGlob.IsMatch("docs/private", "docs/private/notes.md"));
        Assert.True(PathGlob.IsMatch("docs/private/", "docs/private/a/b.md"));
        Assert.False(PathGlob.IsMatch("docs/private", "docs/private-notes.md"));
        Assert.True(PathGlob.IsMatch("docs/plan.md", "docs/plan.md"));
        Assert.True(PathGlob.IsMatch("*.draft.md", "docs/a/x.draft.md"));
        Assert.True(PathGlob.IsMatch("docs/**/secret-*.md", "docs/a/b/secret-plan.md"));
        Assert.True(PathGlob.IsMatch("docs/*.md", "docs/a.md"));
        Assert.False(PathGlob.IsMatch("docs/*.md", "docs/a/b.md"));
        Assert.True(PathGlob.IsMatch(".\\Docs\\Private", "docs/private/notes.md"));
        Assert.False(PathGlob.IsMatch("", "docs/a.md"));
    }

    [Fact]
    public async Task WhatLooksLikeASecretOrIsExcludedStaysOnTheMachineAndThePlanSaysWhatWouldGo()
    {
        using var temp = new TempFolder();
        var input = Path.Combine(temp.Path, "repo");
        var output = Path.Combine(temp.Path, "out");
        SyntheticRepository.Write(input);
        Directory.CreateDirectory(Path.Combine(input, "docs", "private"));
        File.WriteAllText(Path.Combine(input, "docs", "deploy.md"), "# Deploying\n\nRun the script with the token:\n\n    export GH_TOKEN=" + Token + "\n\nThen wait for walrus-harbour-17.\n");
        File.WriteAllText(Path.Combine(input, ".env"), "DB_NAME=walrus-harbour-18\n");
        File.WriteAllText(Path.Combine(input, "docs", "private", "notes.md"), "# Private notes\n\nWe decided to keep walrus-harbour-19 between the two of us.\n");
        File.WriteAllText(Path.Combine(input, "docs", "howto-keys.md"), "# Keys\n\nPut the key in the vault. Example: `export GH_TOKEN=<your token>`. Remember walrus-harbour-20.\n");
        Git(input, "init -q");
        Git(input, "add .");
        Git(input, "-c user.name=Test -c user.email=test@example.com -c commit.gpgsign=false commit -q -m \"docs: the first notes\"");
        File.AppendAllText(Path.Combine(input, "docs", "howto-keys.md"), "\nMore.\n");
        Git(input, "add .");
        Git(input, "-c user.name=Test -c user.email=test@example.com -c commit.gpgsign=false commit -q -m \"fix: we decided to rotate the token " + Token + " at once\"");

        // 1. The plan: worked out here, with no call and nothing written.
        var (options, services, reader, judge) = Setup(input, output, [], []);
        var plan = new RepositorySurvey(options, services).Plan();
        Assert.Equal(0, reader.Calls);
        Assert.Equal(0, judge.Calls);
        Assert.False(Directory.Exists(output));
        Assert.Equal(["reader", "judge"], plan["providers"]!.AsArray().Select(p => p!["role"]!.GetValue<string>()));
        Assert.Equal("api.xiaomimimo.com", plan["providers"]![0]!["host"]!.GetValue<string>());
        Assert.NotEmpty(plan["providers"]![0]!["models"]!.AsArray());
        var toSend = plan["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).ToList();
        Assert.Contains("docs/howto-keys.md", toSend);
        Assert.Contains("docs/private/notes.md", toSend);
        Assert.DoesNotContain("docs/deploy.md", toSend);
        Assert.DoesNotContain(".env", toSend);
        Assert.All(plan["files"]!.AsArray(), f => Assert.True(f!["size"]!.GetValue<long>() > 0));
        var kept = plan["notSent"]!["secrets"]!.AsArray();
        var deploy = Assert.Single(kept, k => k!["path"]!.GetValue<string>() == "docs/deploy.md")!;
        Assert.Equal(("github-token", 5), (deploy["rule"]!.GetValue<string>(), deploy["line"]!.GetValue<int>()));
        Assert.Equal("env-file", Assert.Single(kept, k => k!["path"]!.GetValue<string>() == ".env")!["rule"]!.GetValue<string>());
        Assert.Equal("github-token", Assert.Single(plan["notSent"]!["commits"]!.AsArray())!["rule"]!.GetValue<string>());
        Assert.Equal(1, plan["text"]!["commitMessages"]!.GetValue<int>());
        Assert.True(plan["text"]!["bytes"]!.GetValue<long>() > 0);
        Assert.True(plan["estimate"]!["readerUsd"]!["high"]!.GetValue<double>() >= plan["estimate"]!["readerUsd"]!["low"]!.GetValue<double>());
        Assert.Equal(40m, plan["ceilings"]!["readerUsd"]!.GetValue<decimal>());
        Assert.DoesNotContain(Token, plan.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("2 file(s) that look like they hold a secret", ConsentPlan.Line(plan), StringComparison.Ordinal);

        // 2. The run: nothing of the withheld file, the environment file or the commit message goes to a provider or into the output.
        var first = await new RepositorySurvey(options, services).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, first.Outcome);
        string[] secrets = [Token, "walrus-harbour-17", "walrus-harbour-18"];
        Assert.DoesNotContain(reader.Sent, text => secrets.Any(s => text.Contains(s, StringComparison.Ordinal)));
        Assert.DoesNotContain(judge.Sent, text => secrets.Any(s => text.Contains(s, StringComparison.Ordinal)));
        Assert.DoesNotContain(TextsUnder(output), text => secrets.Any(s => text.Contains(s, StringComparison.Ordinal)));
        Assert.Contains(reader.Sent, text => text.Contains("walrus-harbour-20", StringComparison.Ordinal));
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var cards = DocumentCatalogue.Load(state);
            Assert.DoesNotContain(cards, c => c.Path is "docs/deploy.md" or ".env");
            Assert.Contains(cards, c => c.Path == "docs/private/notes.md");
            Assert.Contains(cards, c => c.Path == "docs/howto-keys.md");
            Assert.Contains(VerifyPhase.Load(state)!.Findings, f => f.StartsWith("2 file(s) not read because they look like they hold a secret", StringComparison.Ordinal) && f.Contains("docs/deploy.md (github-token at line 5)", StringComparison.Ordinal));
            Assert.Contains(DecisionCatalogue.Load(state), d => d.Text.Contains("walrus-harbour-19", StringComparison.Ordinal));
        }

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!;
        Assert.Equal(2, manifest["withheld"]!["secrets"]!.AsArray().Count);
        Assert.Single(manifest["withheld"]!["commits"]!.AsArray());
        Assert.Equal("it looks like it holds a secret: github-token at line 5",
            manifest["files"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == "docs/deploy.md")!["notRead"]!.GetValue<string>());
        Assert.Contains("2 file(s) not read, it looks like it holds a secret", File.ReadAllText(Path.Combine(output, "summary.md")), StringComparison.Ordinal);

        // 3. The person leaves a folder out and allows the file the scan withheld. What an earlier run made of the folder
        // leaves the bundle, its name is shown to no provider, and the allowed file is read.
        var (again, servicesAgain, readerAgain, judgeAgain) = Setup(input, output, ["docs/private"], ["docs/deploy.md"]);
        var second = await new RepositorySurvey(again, servicesAgain).RunAsync(CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, second.Outcome);
        Assert.DoesNotContain(readerAgain.Sent, text => text.Contains("walrus-harbour-19", StringComparison.Ordinal) || text.Contains("docs/private", StringComparison.Ordinal));
        Assert.DoesNotContain(judgeAgain.Sent, text => text.Contains("walrus-harbour-19", StringComparison.Ordinal) || text.Contains("docs/private", StringComparison.Ordinal));
        Assert.Contains(readerAgain.Sent, text => text.Contains("walrus-harbour-17", StringComparison.Ordinal));
        using (var state = RunState.Open(Path.Combine(output, "state.sqlite")))
        {
            var cards = DocumentCatalogue.Load(state);
            Assert.DoesNotContain(cards, c => c.Path == "docs/private/notes.md");
            Assert.Contains(cards, c => c.Path == "docs/deploy.md");
            Assert.DoesNotContain(DecisionCatalogue.Load(state), d => d.Text.Contains("walrus-harbour-19", StringComparison.Ordinal));
        }

        Assert.False(File.Exists(Path.Combine(output, "documents", "docs", "private", "notes.md")));
        foreach (var catalogue in new[] { "documents.json", "decisions.json", "items.json", "graph.json", "questions.json", "manifest.json", "summary.md" })
        {
            Assert.DoesNotContain("walrus-harbour-19", File.ReadAllText(Path.Combine(output, catalogue)), StringComparison.Ordinal);
        }

        foreach (var folder in new[] { "documents", "decisions", "items" }.Select(f => Path.Combine(output, f)).Where(Directory.Exists))
        {
            Assert.DoesNotContain(TextsUnder(folder), text => text.Contains("walrus-harbour-19", StringComparison.Ordinal));
        }

        manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!;
        var excluded = Assert.Single(manifest["withheld"]!["excluded"]!.AsArray())!;
        Assert.Equal(("docs/private/notes.md", "docs/private"), (excluded["path"]!.GetValue<string>(), excluded["pattern"]!.GetValue<string>()));
        var allowed = Assert.Single(manifest["withheld"]!["allowed"]!.AsArray())!;
        Assert.Equal(("docs/deploy.md", "github-token"), (allowed["path"]!.GetValue<string>(), allowed["rule"]!.GetValue<string>()));
        Assert.Equal(".env", Assert.Single(manifest["withheld"]!["secrets"]!.AsArray())!["path"]!.GetValue<string>());
    }

    private static IEnumerable<string> TextsUnder(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var text = new StreamReader(stream, Encoding.Latin1);
            yield return text.ReadToEnd();
        }
    }

    private static void Git(string root, string arguments)
    {
        var start = new ProcessStartInfo("git", arguments) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments[..Math.Min(20, arguments.Length)]}: {process.StandardError.ReadToEnd()}");
    }

    private static (SurveyOptions Options, RunServices Services, ScriptedChatClient Reader, ScriptedJudge Judge) Setup(string input, string output, string[] exclude, string[] allow)
    {
        var reader = new ScriptedChatClient();
        var judge = new ScriptedJudge();
        var options = new SurveyOptions
        {
            InputPath = input,
            ExcludeFiles = exclude,
            AllowFiles = allow,
            Conductor = new ConductorOptions
            {
                OutputPath = output,
                PlaybookPath = TestPaths.Playbook,
                StopAfterPhase = "verify",
                Reader = new ReaderOptions { Retries = 0 },
            },
            Budgets = new SurveyBudgets { ReaderChunkTokens = 600, ChunkOverlapLines = 3, DocumentChunkTokens = 600, DocumentOverlapChars = 400 },
        };
        var services = new RunServices
        {
            QuestionPolicy = (store, book) => new WrittenQuestionPolicy(store, book),
            Backup = BackupKind.Zip,
            ChatClients = _ => reader,
            JudgeModel = judge,
        };
        return (options, services, reader, judge);
    }
}
