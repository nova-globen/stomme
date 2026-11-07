using System.CommandLine;
using System.Globalization;
using Stomme.Cartographer.Cli;
using Stomme.Cartographer.Survey;
using Stomme.Conductor.Keys;
using Stomme.Conductor.Options;
using Stomme.Conductor.Policies;
using Stomme.Conductor.Runner;

// The console use case (ADR 0001, 0018): switches mapped to the libraries' options, nothing more. Each run prints
// its spend and where the log is; nothing else goes to the console unless --ask or --verbose (the plan). The
// console alone knows the names of the environment variables that hold the keys (ADR 0004, 0022).

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var input = new Option<string>("--input", "-i") { Description = "The repository to read (never written to).", Required = true };
var output = new Option<string>("--output", "-o") { Description = "The atlas folder, what the survey writes (ADR 0005, 0018); must not lie inside the input.", Required = true };
var playbook = new Option<string?>("--playbook") { Description = "The playbook folder (default: playbooks/survey-repository when found from the working folder or the executable, else the playbook embedded in the library)." };
var ask = new Option<bool>("--ask") { Description = "Ask questions in the console as the run goes (ADR 0007); otherwise questions.md is written for the next run." };
var noQuestions = new Option<bool>("--no-questions") { Description = "Record every question and continue with the safest default (the none policy)." };
var noBackup = new Option<bool>("--no-backup") { Description = "Skip the backup zip (ADR 0009)." };
var ceiling = new Option<decimal?>("--ceiling") { Description = "The reader's spending ceiling in US dollars across all runs into this output (default 40, ADR 0024)." };
var judgeCeiling = new Option<decimal?>("--judge-ceiling") { Description = "The judge's spending limit in US dollars across all runs (default 5, ADR 0024)." };
var model = new Option<string?>("--model") { Description = "One reader model for every phase (default: pro for orientation, items, decisions and links; flash for documents and verify)." };
var until = new Option<string?>("--until") { Description = "Stop cleanly after this phase (orientation, items, documents, decisions, links, code-survey, verify); stopping after the last phase that has code is a complete run." };
var rerun = new Option<string[]>("--rerun") { Description = "Forget the units of work of these phases (or * for all) so they run again; the cache still answers unchanged calls (ADR 0017).", AllowMultipleArgumentsPerToken = true };
var thinking = new Option<string?>("--thinking") { Description = "The reader's chain of thought on every call: on, off, or the provider's default when absent (MiMo thinks by default; off is cheaper and faster where it holds)." };
var rerunUnits = new Option<string[]>("--rerun-unit") { Description = "Forget the units of work whose key starts with this (for example document/docs/releasing.md) so they run again.", AllowMultipleArgumentsPerToken = true };
var history = new Option<string?>("--history") { Description = "A clone of the repository's older history, read as a second source of commits and dates (ADR 0021)." };
var draftAll = new Option<bool>("--draft-all") { Description = "Write a draft ADR for every decision the judge confirms, not only the significant ones (ADR 0006)." };
var fresh = new Option<bool>("--fresh") { Description = "Start over: keep the old state.sqlite renamed and begin a new one; without it a run resumes from the state (ADR 0017)." };
var forceSurvey = new Option<bool>("--force-code-survey") { Description = "Run the code survey even when documents are not thin (ADR 0010)." };
var addDocuments = new Option<string[]>("--add-documents") { Description = "A folder outside the input whose documents are read as part of it (ADR 0010).", AllowMultipleArgumentsPerToken = true };
var verbose = new Option<bool>("--verbose", "-v") { Description = "Print progress lines." };
var exclude = new Option<string[]>("--exclude") { Description = "Files to leave out: a path, a folder, or a pattern with *, ** and ?. They are never read, and nothing of them goes to a provider; the output carries only each one's path and the pattern that left it out, in plan.json and manifest.json, so you can see what was kept back (ADR 0028).", AllowMultipleArgumentsPerToken = true };
var allowFile = new Option<string[]>("--allow-file") { Description = "A file the secret scan withheld that may be read after all, by its path (ADR 0028).", AllowMultipleArgumentsPerToken = true };
var planOnly = new Option<bool>("--plan") { Description = "Show what a run would send and to whom, with an estimate of the cost, write it to plan.json in the output folder, and stop: no provider is called (ADR 0028)." };
var spendFile = new Option<string?>("--spend-file") { Description = "The spend file the ceilings apply to (default: spend.json in the output folder)." };

var root = new RootCommand("Surveys a repository of any shape and writes an atlas of what a tracker and a wiki should hold.");
foreach (var option in new Option[] { input, output, playbook, ask, noQuestions, noBackup, ceiling, judgeCeiling, model, until, rerun, rerunUnits, thinking, forceSurvey, addDocuments, history, draftAll, verbose, spendFile, fresh, exclude, allowFile, planOnly })
{
    root.Options.Add(option);
}

root.SetAction(async (parseResult, cancellationToken) =>
{
    var interactive = parseResult.GetValue(ask);
    var silent = parseResult.GetValue(noQuestions);
    var chatty = interactive || parseResult.GetValue(verbose);
    var playbookPath = Locator.FindPlaybook(parseResult.GetValue(playbook));
    var (readerKey, readerSource) = KeyReader.Read(Keys.Reader);
    var (judgeKey, judgeSource) = KeyReader.Read(Keys.Judge);
    if (chatty)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Reader key {Keys.Reader}: {readerSource}; judge key {Keys.Judge}: {judgeSource}; playbook: {playbookPath ?? "embedded"}."));
    }

    var chosen = parseResult.GetValue(model);
    var options = new SurveyOptions
    {
        InputPath = parseResult.GetValue(input)!,
        Conductor = new ConductorOptions
        {
            OutputPath = parseResult.GetValue(output)!,
            PlaybookPath = playbookPath,
            Playbook = playbookPath is null ? SurveyPlaybook.Embedded() : null,
            Reader = new ReaderOptions
            {
                Key = readerKey,
                Thinking = (parseResult.GetValue(thinking) ?? "").ToLowerInvariant() switch
                {
                    "" => ThinkingMode.Default,
                    "on" => ThinkingMode.On,
                    "off" => ThinkingMode.Off,
                    var other => throw new ArgumentException($"--thinking takes on or off, not '{other}'."),
                },
            },
            Judge = new JudgeOptions { Key = judgeKey },
            Models = string.IsNullOrWhiteSpace(chosen) ? SurveyModels.Default : PhaseModels.All(chosen),
            Spending = new SpendingOptions
            {
                CeilingUsd = parseResult.GetValue(ceiling) ?? 40m,
                JudgeCeilingUsd = parseResult.GetValue(judgeCeiling) ?? 5m,
                Scope = CeilingScope.AllRuns,
                SpendPath = parseResult.GetValue(spendFile),
            },
            StopAfterPhase = parseResult.GetValue(until),
            RerunPhases = new HashSet<string>(parseResult.GetValue(rerun) ?? [], StringComparer.OrdinalIgnoreCase),
            RerunUnits = parseResult.GetValue(rerunUnits) ?? [],
            Fresh = parseResult.GetValue(fresh),
        },
        ForceCodeSurvey = parseResult.GetValue(forceSurvey),
        ExtraDocumentFolders = parseResult.GetValue(addDocuments) ?? [],
        HistoryPath = parseResult.GetValue(history),
        DraftRoutineDecisions = parseResult.GetValue(draftAll),
        ExcludeFiles = parseResult.GetValue(exclude) ?? [],
        AllowFiles = parseResult.GetValue(allowFile) ?? [],
    };

    if (parseResult.GetValue(planOnly))
    {
        try
        {
            var plan = new RepositorySurvey(options, new RunServices { QuestionPolicy = (store, book) => new NoneQuestionPolicy(book, store) }).Plan();
            Directory.CreateDirectory(options.Conductor.OutputPath);
            var planPath = Path.Combine(options.Conductor.OutputPath, ConsentPlan.FileName);
            File.WriteAllText(planPath, plan.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(ConsentPlan.Line(plan));
            foreach (var kept in plan["notSent"]!["secrets"]!.AsArray())
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  kept back: {kept!["path"]} ({kept["rule"]}{(kept["line"] is null ? "" : ", line " + kept["line"])})"));
            }

            Console.WriteLine("Nothing was sent. The plan is " + planPath + ".");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
        {
            Console.Error.WriteLine("The plan could not be made: " + ex.Message);
            return 1;
        }
    }

    var services = new RunServices
    {
        QuestionPolicy = (store, book) => silent
            ? new NoneQuestionPolicy(book, store)
            : interactive
                ? new InteractiveQuestionPolicy(store, book, ConsoleQuestions.AskAsync)
                : new WrittenQuestionPolicy(store, book),
        Backup = parseResult.GetValue(noBackup) ? BackupKind.None : BackupKind.Zip,
        Progress = chatty ? line => Console.WriteLine(line) : null,
    };

    try
    {
        var result = await new RepositorySurvey(options, services).RunAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{result.Outcome}: {result.Message}. Spent {PriceTable.Dollars(result.SpentUsd)} this run, {PriceTable.Dollars(result.AllRunsUsd)} across all runs. {result.OpenQuestions} open question(s). The log is {Path.Combine(result.OutputPath, "log.md")}."));
        return result.Outcome is RunOutcome.Completed or RunOutcome.StoppedAfterPhase ? 0 : 2;
    }
    catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
    {
        Console.Error.WriteLine("The run could not start: " + ex.Message);
        return 1;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // The run's log and summary already hold the failure (the engine writes them before it throws); the state keeps every unit done, so a new run resumes.
        Console.Error.WriteLine("The run failed: " + ex.Message + " The log and the summary in the output folder have the details; a new run resumes from the state.");
        return 2;
    }
});

var probe = new Command("probe", "One tiny call to each API to prove the keys reach the process; prints figures only, never a key.");
var probeOutput = new Option<string>("--output", "-o") { Description = "A folder for the probe's cache and log.", Required = true };
probe.Options.Add(probeOutput);
probe.SetAction(async (parseResult, cancellationToken) =>
    await Probe.RunAsync(parseResult.GetValue(probeOutput)!, cancellationToken).ConfigureAwait(false));
root.Subcommands.Add(probe);

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
