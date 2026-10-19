using System;
using System.Collections.Generic;
using Multiplayer.Core.Session;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Session;

[TestFixture]
public sealed class LocalRequestQueueTests
{
    private sealed class Player { }
    private sealed class Switch { public int Branch; }
    private sealed class Rotate { public float Angle; }

    [Test]
    public void Submit_RefusesARequestNothingHandles()
    {
        var queue = new LocalRequestQueue<Player>();
        Assert.That(queue.Submit(new Switch()), Is.False);
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public void Drain_HandlesEveryRequestInOrder_WithThePlayerAsSender()
    {
        var queue = new LocalRequestQueue<Player>();
        var host = new Player();
        var seen = new List<string>();
        queue.Handle<Switch>((s, p) => { Assert.That(p, Is.SameAs(host)); seen.Add("switch " + s.Branch); });
        queue.Handle<Rotate>((r, p) => seen.Add("rotate " + r.Angle));

        queue.Submit(new Switch { Branch = 1 });
        queue.Submit(new Rotate { Angle = 90 });
        queue.Submit(new Switch { Branch = 0 });
        int handled = queue.Drain(host, (_, _) => Assert.Fail("nothing should fail"));

        Assert.That(handled, Is.EqualTo(3));
        Assert.That(seen, Is.EqualTo(new[] { "switch 1", "rotate 90", "switch 0" }));
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public void Drain_OneHandlerThrowing_DoesNotStopTheOthers()
    {
        var queue = new LocalRequestQueue<Player>();
        var failures = new List<(Type, Exception)>();
        int rotations = 0;
        queue.Handle<Switch>((_, _) => throw new InvalidOperationException("broken"));
        queue.Handle<Rotate>((_, _) => rotations++);

        queue.Submit(new Switch());
        queue.Submit(new Rotate());
        int handled = queue.Drain(new Player(), (type, e) => failures.Add((type, e)));

        Assert.That(handled, Is.EqualTo(1));
        Assert.That(rotations, Is.EqualTo(1));
        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0].Item1, Is.EqualTo(typeof(Switch)));
        Assert.That(failures[0].Item2, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Drain_WithNoPlayer_DropsTheRequestsAndReportsThem()
    {
        var queue = new LocalRequestQueue<Player>();
        int handled = 0;
        var dropped = new List<Type>();
        queue.Handle<Switch>((_, _) => handled++);

        queue.Submit(new Switch());
        queue.Submit(new Switch());
        queue.Drain(null, (type, e) => { Assert.That(e, Is.Null); dropped.Add(type); });

        Assert.That(handled, Is.Zero);
        Assert.That(dropped, Is.EqualTo(new[] { typeof(Switch), typeof(Switch) }));
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public void Handle_ReplacesAnEarlierHandler()
    {
        var queue = new LocalRequestQueue<Player>();
        string which = null;
        queue.Handle<Switch>((_, _) => which = "first");
        queue.Handle<Switch>((_, _) => which = "second");

        queue.Submit(new Switch());
        queue.Drain(new Player(), (_, _) => { });

        Assert.That(which, Is.EqualTo("second"));
    }

    [Test]
    public void RequestsSubmittedWhileDraining_AreHandledInTheSameDrain()
    {
        var queue = new LocalRequestQueue<Player>();
        var seen = new List<int>();
        queue.Handle<Switch>((s, _) =>
        {
            seen.Add(s.Branch);
            if (s.Branch == 1)
                queue.Submit(new Switch { Branch = 2 });
        });

        queue.Submit(new Switch { Branch = 1 });
        queue.Drain(new Player(), (_, _) => { });

        Assert.That(seen, Is.EqualTo(new[] { 1, 2 }));
    }
}
