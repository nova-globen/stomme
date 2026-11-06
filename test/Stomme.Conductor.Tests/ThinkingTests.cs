using Stomme.Conductor.Models;
using Stomme.Conductor.Options;
using Stomme.Conductor.State;

namespace Stomme.Conductor.Tests;

public sealed class ThinkingTests
{
    [Fact]
    public void ThePolicySetsTheThinkingFieldOnTheRequestBody()
    {
        var off = ThinkingPolicy.Rewrite("{\"model\":\"m\",\"messages\":[]}", ThinkingMode.Off);
        Assert.Equal("{\"model\":\"m\",\"messages\":[],\"thinking\":{\"type\":\"disabled\"}}", off);

        var on = ThinkingPolicy.Rewrite("{\"model\":\"m\",\"thinking\":{\"type\":\"disabled\"}}", ThinkingMode.On);
        Assert.Equal("{\"model\":\"m\",\"thinking\":{\"type\":\"enabled\"}}", on);
    }

    [Fact]
    public void ThePolicyLeavesOtherBodiesAlone()
    {
        Assert.Null(ThinkingPolicy.Rewrite("{\"model\":\"m\"}", ThinkingMode.Default));
        Assert.Null(ThinkingPolicy.Rewrite("[1, 2]", ThinkingMode.Off));
        Assert.Null(ThinkingPolicy.Rewrite("not json", ThinkingMode.Off));
        Assert.Null(ThinkingPolicy.Rewrite("", ThinkingMode.Off));
    }

    [Fact]
    public void UnitsAreForgottenByKeyPrefix()
    {
        using var temp = new TempFolder();
        using var state = RunState.Open(Path.Combine(temp.Path, "state.sqlite"));
        state.BeginRun("run-1", "0.4.0", "hash");
        state.CompleteUnit("documents", "run-1", "document/docs/releasing.md#1", "h1", 0.01m, null);
        state.CompleteUnit("documents", "run-1", "document/docs/releasing.md#2", "h2", 0.01m, null);
        state.CompleteUnit("documents", "run-1", "document/docs/release-notes.md", "h3", 0.01m, null);

        state.ForgetUnitsByPrefix("document/docs/releasing.md");

        Assert.Null(state.DoneUnit("documents", "document/docs/releasing.md#1", "h1"));
        Assert.Null(state.DoneUnit("documents", "document/docs/releasing.md#2", "h2"));
        Assert.NotNull(state.DoneUnit("documents", "document/docs/release-notes.md", "h3"));
    }
}
