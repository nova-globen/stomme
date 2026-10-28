using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines.Tests.Flowchart;
public class FlowGraphTests
{
    // Start
    //   ↓
    //   A ← W    Y
    //  ↙ ↘      ↙
    // B   C ← X ← Z
    //  ↘ ↙
    //   D
    //   ↓
    //  End
    [Fact]
    public void InvalidDanglingStepsTest()
    {
        var start = new TestStep("start");
        var a = new TestStep("a");
        var b = new TestStep("b");
        var c = new TestStep("c");
        var d = new TestStep("d");
        var end = new TestStep("end");
        var w = new TestStep("w");
        var x = new TestStep("x");
        var y = new TestStep("y");
        var z = new TestStep("z");

        var connections = new List<Connection>
        {
            new(start, a),
            new(a, b),
            new(a, c),
            new(b, d),
            new(c, d),
            new(d, end),
            new(w, a), // invalid dangling connector
            new(x, c), // invalid dangling connector
            new(y, x), // invalid dangling connector
            new(z, x), // invalid dangling connector
       };

        var flowGraph = new FlowGraph(connections, start);

        // FlowGraph.GetForwardInboundConnections
        flowGraph.ValidateForwardInboundConnections(["start->a"], a);
        flowGraph.ValidateForwardInboundConnections(["a->b"], b);
        flowGraph.ValidateForwardInboundConnections(["a->c"], c);
        flowGraph.ValidateForwardInboundConnections(["b->d", "c->d"], d);
        flowGraph.ValidateForwardInboundConnections(["d->end"], end);
        flowGraph.ValidateForwardInboundConnections([], x);

        // FlowGraph.GetOutboundConnections
        flowGraph.ValidateOutboundConnections(["start->a"], start);
        flowGraph.ValidateOutboundConnections(["a->b", "a->c"], a);
        flowGraph.ValidateOutboundConnections(["b->d"], b);
        flowGraph.ValidateOutboundConnections(["c->d"], c);
        flowGraph.ValidateOutboundConnections(["d->end"], d);
        flowGraph.ValidateOutboundConnections([], end);
        flowGraph.ValidateOutboundConnections(["x->c"], x);

        // FlowGraph.GetAncestorSteps
        flowGraph.ValidateAncestorSteps([], start);
        flowGraph.ValidateAncestorSteps([start], a);
        flowGraph.ValidateAncestorSteps([a, start], b);
        flowGraph.ValidateAncestorSteps([a, start], c);
        flowGraph.ValidateAncestorSteps([b, c, a, start], d);
        flowGraph.ValidateAncestorSteps([], x);

        // FlowGraph IsDanglingStep
        flowGraph.ValidateDanglingStep(false, start);
        flowGraph.ValidateDanglingStep(false, a);
        flowGraph.ValidateDanglingStep(false, b);
        flowGraph.ValidateDanglingStep(false, c);
        flowGraph.ValidateDanglingStep(false, d);
        flowGraph.ValidateDanglingStep(false, end);
        flowGraph.ValidateDanglingStep(true, w);
        flowGraph.ValidateDanglingStep(true, x);
        flowGraph.ValidateDanglingStep(true, y);
        flowGraph.ValidateDanglingStep(true, z);
    }

    // Start
    //   ↓
    //   A ← ←
    //  ↙ ↘    ↖
    // B   C   ↑
    //  ↘ ↙    ↗ 
    //   D → → 
    [Fact]
    public void ValidBackwardConnectionTest()
    {
        var start = new TestStep("start");
        var a = new TestStep("a");
        var b = new TestStep("b");
        var c = new TestStep("c");
        var d = new TestStep("d");

        var connections = new List<Connection>
        {
            new(start, a),
            new(a, b),
            new(a, c),
            new(b, d),
            new(c, d),
            new(d, a), // loopback
        };

        var flowGraph = new FlowGraph(connections, start);

        // FlowGraph.GetForwardInboundConnections
        flowGraph.ValidateForwardInboundConnections([], start);
        flowGraph.ValidateForwardInboundConnections(["start->a"], a);
        flowGraph.ValidateForwardInboundConnections(["a->b"], b);
        flowGraph.ValidateForwardInboundConnections(["a->c"], c);
        flowGraph.ValidateForwardInboundConnections(["b->d", "c->d"], d);

        // FlowGraph.GetOutboundConnections
        flowGraph.ValidateOutboundConnections(["start->a"], start);
        flowGraph.ValidateOutboundConnections(["a->b", "a->c"], a);
        flowGraph.ValidateOutboundConnections(["b->d"], b);
        flowGraph.ValidateOutboundConnections(["c->d"], c);
        flowGraph.ValidateOutboundConnections(["d->a"], d);

        // FlowGraph.GetAncestorSteps
        flowGraph.ValidateAncestorSteps([], start);
        flowGraph.ValidateAncestorSteps([start], a);
        flowGraph.ValidateAncestorSteps([a, start], b);
        flowGraph.ValidateAncestorSteps([a, start], c);
        flowGraph.ValidateAncestorSteps([b, c, a, start], d);

        // FlowGraph.IsBackwardConnection
        flowGraph.ValidateBackwardConnection(false, false, new(a, c));
        flowGraph.ValidateBackwardConnection(true, true, new(d, a));
    }


    // Start
    //  ↙ ↘
    // A   B ← ←
    //  ↘ ↙ ↘    ↖
    //   C   D ↰ ↑
    //    ↘ ↙  ↗ ↗ 
    //     E → → 
    [Fact]
    public void InvalidLoopbackTest()
    {
        var start = new TestStep("start");
        var a = new TestStep("a");
        var b = new TestStep("b");
        var c = new TestStep("c");
        var d = new TestStep("d");
        var e = new TestStep("e");

        var connections = new List<Connection>
        {
            new(start, a),
            new(start, b),
            new(a, c),
            new(b, c),
            new(b, d),
            new(d, e),
            new(c, e),
            new(e, b), // invalid loopback
            new(e, d), // invalid loopback
        };

        var flowGraph = new FlowGraph(connections, start);

        // FlowGraph.GetForwardInboundConnections
        flowGraph.ValidateForwardInboundConnections([], start);
        flowGraph.ValidateForwardInboundConnections(["start->a"], a);
        flowGraph.ValidateForwardInboundConnections(["start->b"], b);
        flowGraph.ValidateForwardInboundConnections(["a->c", "b->c"], c);
        flowGraph.ValidateForwardInboundConnections(["b->d"], d);
        flowGraph.ValidateForwardInboundConnections(["c->e", "d->e"], e);

        // FlowGraph.GetOutboundConnections
        flowGraph.ValidateOutboundConnections(["start->a", "start->b"], start);
        flowGraph.ValidateOutboundConnections(["a->c"], a);
        flowGraph.ValidateOutboundConnections(["b->c", "b->d"], b);
        flowGraph.ValidateOutboundConnections(["c->e"], c);
        flowGraph.ValidateOutboundConnections(["d->e"], d);
        flowGraph.ValidateOutboundConnections(["e->b", "e->d"], e);

        // FlowGraph.GetAncestorSteps
        flowGraph.ValidateAncestorSteps([], start);
        flowGraph.ValidateAncestorSteps([start], a);
        flowGraph.ValidateAncestorSteps([start], b);
        flowGraph.ValidateAncestorSteps([a, b, start], c);
        flowGraph.ValidateAncestorSteps([b, start], d);
        flowGraph.ValidateAncestorSteps([c, d, a, b, start], e);

        // FlowGraph.IsBackwardConnection
        flowGraph.ValidateBackwardConnection(true, false, new(e, b));
        flowGraph.ValidateBackwardConnection(true, false, new(e, d));
    }

    // Start
    //  ↙ ↘
    // ↓   A
    // ↓   ↓ 
    // ↓   B
    // ↓ ↙ ↓ 
    // C   D
    //  ↘ ↙
    //  End
    [Fact]
    public void LongLegTest()
    {
        var start = new TestStep("start");
        var a = new TestStep("a");
        var b = new TestStep("b");
        var c = new TestStep("c");
        var d = new TestStep("d");
        var end = new TestStep("end");

        var connections = new List<Connection>
        {
            new(start, a),
            new(start, c),
            new(a, b),
            new(b, c),
            new(b, d),
            new(c, end),
            new(d, end),
        };

        var flowGraph = new FlowGraph(connections, start);

        // FlowGraph.GetForwardInboundConnections
        flowGraph.ValidateForwardInboundConnections([], start);
        flowGraph.ValidateForwardInboundConnections(["start->a"], a);
        flowGraph.ValidateForwardInboundConnections(["a->b"], b);
        flowGraph.ValidateForwardInboundConnections(["start->c", "b->c"], c);
        flowGraph.ValidateForwardInboundConnections(["b->d"], d);
        flowGraph.ValidateForwardInboundConnections(["c->end", "d->end"], end);

        // FlowGraph.GetOutboundConnections
        flowGraph.ValidateOutboundConnections(["start->a", "start->c"], start);
        flowGraph.ValidateOutboundConnections(["a->b"], a);
        flowGraph.ValidateOutboundConnections(["b->c", "b->d"], b);
        flowGraph.ValidateOutboundConnections(["c->end"], c);
        flowGraph.ValidateOutboundConnections(["d->end"], d);
        flowGraph.ValidateOutboundConnections([], end);

        // FlowGraph.GetAncestorSteps
        flowGraph.ValidateAncestorSteps([], start);
        flowGraph.ValidateAncestorSteps([start], a);
        flowGraph.ValidateAncestorSteps([a, start], b);
        flowGraph.ValidateAncestorSteps([start, b, a], c);
        flowGraph.ValidateAncestorSteps([b, a, start], d);
        flowGraph.ValidateAncestorSteps([c, d, start, b, a], end);
    }

    //      Start
    //       ↙ ↘
    //      A   B
    //      ↓↘ ↙ ↘ 
    //      ↳→C   D 
    //        ↓↘ ↙ 
    //        ↳→E
    [Fact]
    public void SameEdgeDuplicateTest()
    {
        var start = new TestStep("start");
        var a = new TestStep("a");
        var b = new TestStep("b");
        var c = new TestStep("c");
        var d = new TestStep("d");
        var e = new TestStep("e");

        var connections = new List<Connection>
        {
            new(start, a),
            new(start, b),
            new(a, c),
            new(a, c), // duplicate
            new(b, c),
            new(b, d),
            new(d, e),
            new(c, e),
            new(c, e), // duplicate
        };

        var flowGraph = new FlowGraph(connections, start);

        // FlowGraph.GetForwardInboundConnections
        flowGraph.ValidateForwardInboundConnections([], start);
        flowGraph.ValidateForwardInboundConnections(["start->a"], a);
        flowGraph.ValidateForwardInboundConnections(["start->b"], b);
        flowGraph.ValidateForwardInboundConnections(["a->c", "b->c"], c);
        flowGraph.ValidateForwardInboundConnections(["b->d"], d);
        flowGraph.ValidateForwardInboundConnections(["c->e", "d->e"], e);

        // FlowGraph.GetOutboundConnections
        flowGraph.ValidateOutboundConnections(["start->a", "start->b"], start);
        flowGraph.ValidateOutboundConnections(["a->c"], a);
        flowGraph.ValidateOutboundConnections(["b->c", "b->d"], b);
        flowGraph.ValidateOutboundConnections(["c->e"], c);
        flowGraph.ValidateOutboundConnections(["d->e"], d);
        flowGraph.ValidateOutboundConnections([], e);

        // FlowGraph.GetAncestorSteps
        flowGraph.ValidateAncestorSteps([], start);
        flowGraph.ValidateAncestorSteps([start], a);
        flowGraph.ValidateAncestorSteps([start], b);
        flowGraph.ValidateAncestorSteps([a, b, start], c);
        flowGraph.ValidateAncestorSteps([b, start], d);
        flowGraph.ValidateAncestorSteps([c, d, a, b, start], e);
    }

    //      Start
    //       ↙ ↘
    //      A   B
    //      ↓↘ ↙ ↘ 
    //      ↳→C   D 
    //        ↓↘ ↙ 
    //        ↳→E
    [Fact]
    public void SameEdgeDifferentPortDuplicateTest()
    {
        var start = new TestStep("start");
        var a = new TestStep("a");
        var b = new TestStep("b");
        var c = new TestStep("c");
        var d = new TestStep("d");
        var e = new TestStep("e");

        var connections = new List<Connection>
        {
            new(start, a),
            new(start, b),
            new(start, b), // duplicate
            new(a, c),
            new(new Endpoint(a, "Yes"), new Endpoint(c)),
            new(new Endpoint(a, "Yes"), new Endpoint(c)), // duplicate
            new(b, c),
            new(b, d),
            new(c, e),
            new(new Endpoint(c, "Yes"), new Endpoint(e)),
            new(d, e),
        };

        var flowGraph = new FlowGraph(connections, start);

        // FlowGraph.GetForwardInboundConnections
        flowGraph.ValidateForwardInboundConnections([], start);
        flowGraph.ValidateForwardInboundConnections(["start->a"], a);
        flowGraph.ValidateForwardInboundConnections(["start->b"], b);
        flowGraph.ValidateForwardInboundConnections(["a->c", "a:Yes->c", "b->c"], c);
        flowGraph.ValidateForwardInboundConnections(["b->d"], d);
        flowGraph.ValidateForwardInboundConnections(["c->e", "c:Yes->e", "d->e"], e);

        // FlowGraph.GetOutboundConnections
        flowGraph.ValidateOutboundConnections(["start->a", "start->b"], start);
        flowGraph.ValidateOutboundConnections(["a->c", "a:Yes->c"], a);
        flowGraph.ValidateOutboundConnections(["b->c", "b->d"], b);
        flowGraph.ValidateOutboundConnections(["c->e", "c:Yes->e"], c);
        flowGraph.ValidateOutboundConnections(["d->e"], d);
        flowGraph.ValidateOutboundConnections([], e);

        // FlowGraph.GetAncestorSteps
        flowGraph.ValidateAncestorSteps([], start);
        flowGraph.ValidateAncestorSteps([start], a);
        flowGraph.ValidateAncestorSteps([start], b);
        flowGraph.ValidateAncestorSteps([a, b, start], c);
        flowGraph.ValidateAncestorSteps([b, start], d);
        flowGraph.ValidateAncestorSteps([c, d, a, b, start], e);
    }

    class TestStep : Step
    {
        public TestStep(string id)
        {
            Id = id;
            Name = id;
        }

        public override string ToString()
        {
            return Id;
        }
    }
}
