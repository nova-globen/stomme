namespace Stomme.Engine.Pipelines.Steps.Flowchart.Models;

internal class Token(string fromStepId, string? fromStepName, string? outcome, string toStepId, string? toStepName, bool consumed, bool blocked)
{
    public static Token Create(IStep from, IStep to, string? outcome) => new(from.Id, from.Name, outcome, to.Id, to.Name, false, false);

    public string FromStepId { get; } = fromStepId;
    public string? FromStepName { get; } = fromStepName;
    public string? Outcome { get; } = outcome;
    public string ToStepId { get; } = toStepId;
    public string? ToStepName { get; } = toStepName;
    public bool Consumed { get; private set; } = consumed;
    public bool Blocked { get; private set; } = blocked;

    public Token Consume()
    {
        Consumed = true;
        return this;
    }

    public Token Block()
    {
        Blocked = true;
        return this;
    }
}