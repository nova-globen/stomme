using System.Globalization;
using System.Text;
using Stomme.Conductor.Options;
using Stomme.Conductor.Runner;

namespace Stomme.Conductor.Ledger;

/// <summary>Writes state.md, "what I know so far", rendered from the state while a run is going (ADR 0017); the host adds its own sections.</summary>
public static class StateWriter
{
    public static void Write(RunContext context, string doingNow)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = context.State;
        var text = new StringBuilder();
        text.AppendLine("# What I know so far");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Rendered {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} during run {context.RunId} (run {state.RunCount} into this output folder). The state itself is `state.sqlite`.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Doing now: {doingNow}");
        text.AppendLine();
        text.AppendLine("## Inputs");
        text.AppendLine();
        if (context.Diff is { } diff)
        {
            text.AppendLine(diff.IsFirstRun
                ? string.Create(CultureInfo.InvariantCulture, $"- First run into this state: {diff.Added} inputs recorded.")
                : string.Create(CultureInfo.InvariantCulture, $"- Against the state: {diff.Unchanged} unchanged (everything derived from them kept), {diff.Changed} changed and {diff.Added} added (their units redone), {diff.Gone} gone (marked, never deleted)."));
        }
        else
        {
            text.AppendLine("- Not reconciled with the state yet.");
        }

        text.AppendLine();
        context.Host.DescribeState(context, text);
        text.AppendLine("## Progress by phase (units of work)");
        text.AppendLine();
        var counts = state.UnitCounts();
        foreach (var phase in context.Playbook.Phases)
        {
            var (done, usd) = counts.TryGetValue(phase, out var c) ? c : (0, 0m);
            var status = context.PhasesDone.Contains(phase) ? "done" : done > 0 ? "in progress or partly done" : "not started";
            text.AppendLine(CultureInfo.InvariantCulture, $"- {phase}: {status}; {done} units done ({PriceTable.Dollars(usd)})");
        }

        text.AppendLine();
        text.AppendLine("## The glossary");
        text.AppendLine();
        var glossary = state.Glossary();
        text.AppendLine(CultureInfo.InvariantCulture, $"{glossary.Count} entries within a budget of {context.Glossary.BudgetTokens} tokens.");
        foreach (var group in glossary.GroupBy(g => g.Kind))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key}: {string.Join("; ", group.Take(12).Select(g => g.Term))}{(group.Count() > 12 ? "; ..." : "")}");
        }

        text.AppendLine();
        text.AppendLine("## Questions");
        text.AppendLine();
        var questions = state.Questions();
        text.AppendLine(CultureInfo.InvariantCulture, $"{questions.Count(q => q.Answer is null)} open, {questions.Count(q => q.Answer is not null)} answered (in `questions.md` and `answers.md`).");
        text.AppendLine();
        text.AppendLine("## Spend");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"This run {PriceTable.Dollars(context.Spend.RunUsd)} over {context.Spend.RunCalls} calls (reader {PriceTable.Dollars(context.Spend.RunUsdOf(SpendKind.Reader))}, judge {PriceTable.Dollars(context.Spend.RunUsdOf(SpendKind.Judge))}); all runs: reader {PriceTable.Dollars(context.Spend.AllRunsUsdOf(SpendKind.Reader))} of the {PriceTable.Dollars(context.Options.Spending.CeilingUsd)} ceiling, judge {PriceTable.Dollars(context.Spend.AllRunsUsdOf(SpendKind.Judge))} of the {PriceTable.Dollars(context.Options.Spending.JudgeCeilingUsd)} limit.");
        File.WriteAllText(context.Paths.State, text.ToString(), new UTF8Encoding(false));
    }
}
