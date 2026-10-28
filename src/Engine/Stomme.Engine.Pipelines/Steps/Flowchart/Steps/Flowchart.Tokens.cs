using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Signals;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Steps;

public partial class Flowchart
{
    private const string TokenStoreKey = "Flowchart.Tokens";

    private async ValueTask OnChildCompletedTokenBasedLogicAsync(StepCompletedContext ctx)
    {
        var flowContext = ctx.TargetContext;
        var completedStep = ctx.ChildContext.Step;
        var flowGraph = flowContext.GetFlowGraph();
        var tokens = GetTokenList(flowContext);

        // If the completed step is a terminal node, complete the flowchart immediately.
        if (completedStep is ITerminalNode)
        {
            tokens.Clear();
            await flowContext.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        // Emit tokens for active outcomes.
        var outcomes = (ctx.Result as Outcomes ?? Outcomes.Default).Names;
        var outboundConnections = flowGraph.GetOutboundConnections(completedStep);
        var activeOutboundConnections = outboundConnections.Where(x => outcomes.Contains(x.Source.Port)).Distinct().ToList();

        foreach (var connection in activeOutboundConnections)
            tokens.Add(Token.Create(connection.Source.Step, connection.Target.Step, connection.Source.Port));

        // Consume inbound tokens to the completed step.
        var inboundTokens = tokens.Where(t => t.ToStepId == completedStep.Id && t is { Consumed: false, Blocked: false }).ToList();
        foreach (var t in inboundTokens)
            t.Consume();

        // Schedule next steps based on merge modes.
        foreach (var connection in activeOutboundConnections)
        {
            var targetStep = connection.Target.Step;
            var mergeMode = await targetStep.GetMergeModeAsync(ctx.ChildContext).ConfigureAwait(false);

            switch (mergeMode)
            {
                case MergeMode.Cascade:
                case MergeMode.Race:
                    if (mergeMode == MergeMode.Race)
                        await flowContext.CancelInboundAncestorsAsync(targetStep).ConfigureAwait(false);

                    // Check for existing blocked token on this specific connection.
                    var existingBlockedToken = tokens.FirstOrDefault(t =>
                        t.ToStepId == targetStep.Id &&
                        t.FromStepId == connection.Source.Step.Id &&
                        t.Outcome == connection.Source.Port &&
                        t.Blocked);

                    if (existingBlockedToken == null)
                    {
                        // Schedule the target.
                        var options = new ScheduleWorkOptions
                        {
                            CompletionCallback = OnChildCompletedTokenBasedLogicAsync,
                            SchedulingStepExecutionId = ctx.ChildContext.Id
                        };
                        await flowContext.ScheduleStepAsync(targetStep, options).ConfigureAwait(false);

                        // Block other inbound connections (adjust per mode if needed).
                        var otherInboundConnections = flowGraph.GetForwardInboundConnections(targetStep)
                            .Where(x => x.Source.Step != completedStep)
                            .ToList();

                        foreach (var inboundConnection in otherInboundConnections)
                        {
                            var blockedToken = Token.Create(inboundConnection.Source.Step, inboundConnection.Target.Step, inboundConnection.Source.Port).Block();
                            tokens.Add(blockedToken);
                        }
                    }
                    else
                    {
                        // Consume the block without scheduling.
                        existingBlockedToken.Consume();
                    }

                    break;

                case MergeMode.Merge:
                    // Wait for tokens from all forward inbound connections.
                    // Unlike Converge, this ignores backward connections (loops).
                    // Schedule on arrival for <=1 forward inbound (e.g., loops, sequential).
                    var inboundConnectionsMerge = flowGraph.GetForwardInboundConnections(targetStep);

                    if (inboundConnectionsMerge.Count > 1)
                    {
                        var hasAllTokens = inboundConnectionsMerge.All(inbound =>
                            tokens.Any(t =>
                                t is { Consumed: false, Blocked: false } &&
                                t.FromStepId == inbound.Source.Step.Id &&
                                t.ToStepId == targetStep.Id &&
                                t.Outcome == inbound.Source.Port
                            )
                        );

                        if (hasAllTokens)
                        {
                            var options = new ScheduleWorkOptions
                            {
                                CompletionCallback = OnChildCompletedTokenBasedLogicAsync,
                                SchedulingStepExecutionId = ctx.ChildContext.Id
                            };
                            await flowContext.ScheduleStepAsync(targetStep, options).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var options = new ScheduleWorkOptions
                        {
                            CompletionCallback = OnChildCompletedTokenBasedLogicAsync,
                            SchedulingStepExecutionId = ctx.ChildContext.Id
                        };
                        await flowContext.ScheduleStepAsync(targetStep, options).ConfigureAwait(false);
                    }

                    break;

                case MergeMode.Converge:
                    // Strictest mode: Wait for tokens from ALL inbound connections (forward + backward).
                    // Requires every possible inbound path to execute before proceeding.
                    var allInboundConnectionsConverge = flowGraph.GetInboundConnections(targetStep);

                    if (allInboundConnectionsConverge.Count > 1)
                    {
                        var hasAllTokens = allInboundConnectionsConverge.All(inbound =>
                            tokens.Any(t =>
                                t is { Consumed: false, Blocked: false } &&
                                t.FromStepId == inbound.Source.Step.Id &&
                                t.ToStepId == targetStep.Id &&
                                t.Outcome == inbound.Source.Port
                            )
                        );

                        if (hasAllTokens)
                        {
                            var options = new ScheduleWorkOptions
                            {
                                CompletionCallback = OnChildCompletedTokenBasedLogicAsync,
                                SchedulingStepExecutionId = ctx.ChildContext.Id
                            };
                            await flowContext.ScheduleStepAsync(targetStep, options).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var options = new ScheduleWorkOptions
                        {
                            CompletionCallback = OnChildCompletedTokenBasedLogicAsync,
                            SchedulingStepExecutionId = ctx.ChildContext.Id
                        };
                        await flowContext.ScheduleStepAsync(targetStep, options).ConfigureAwait(false);
                    }

                    break;

                case MergeMode.Stream:
                default:
                    // Flows freely - approximation that proceeds when upstream completes, ignoring dead paths.
                    var inboundConnectionsStream = flowGraph.GetForwardInboundConnections(targetStep);
                    var hasUnconsumed = inboundConnectionsStream.Any(inbound =>
                        tokens.Any(t => !t.Consumed && !t.Blocked && t.ToStepId == inbound.Source.Step.Id)
                    );

                    if (!hasUnconsumed)
                    {
                        var options = new ScheduleWorkOptions
                        {
                            CompletionCallback = OnChildCompletedTokenBasedLogicAsync,
                            SchedulingStepExecutionId = ctx.ChildContext.Id
                        };
                        await flowContext.ScheduleStepAsync(targetStep, options).ConfigureAwait(false);
                    }
                    break;
            }
        }

        // Complete flowchart if no pending work.
        if (!flowContext.HasPendingWork())
        {
            tokens.Clear();
            await flowContext.CompleteStepAsync().ConfigureAwait(false);
        }

        // Purge consumed tokens for the completed step.
        tokens.RemoveWhere(t => t.ToStepId == completedStep.Id && t.Consumed);
    }

    private async ValueTask OnTokenFlowStepCanceledAsync(CancelSignal signal, SignalContext context)
    {
        var flowchartContext = context.ReceiverStepExecutionContext;
        var cancelledStepContext = context.SenderStepExecutionContext;

        // Remove all tokens from and to this step.
        var tokenList = GetTokenList(flowchartContext);
        tokenList.RemoveWhere(x => x.FromStepId == cancelledStepContext.Step.Id || x.ToStepId == cancelledStepContext.Step.Id);
        await CompleteIfNoPendingWorkAsync(flowchartContext).ConfigureAwait(false);
    }

    internal List<Token> GetTokenList(StepExecutionContext context)
    {
        if (context.Properties.TryGetValue(TokenStoreKey, out var obj) && obj is List<Token> list)
            return list;

        var newList = new List<Token>();
        context.Properties[TokenStoreKey] = newList;
        return newList;
    }
}