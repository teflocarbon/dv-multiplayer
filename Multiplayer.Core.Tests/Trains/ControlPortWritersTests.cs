using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class ControlPortWritersTests
{
    private const float Tolerance = 0.02f;
    private const float Settle = 1.5f;

    // A DE6's ports: its cab's levers and windows are interactables; the horn, the reverser's
    // decoder and the compressor signal are CONTROL-typed but set by the simulation.
    private static ControlPortWriters<string> DE6() => new(
        controlPorts: new[]
        {
            "throttle.EXT_IN", "hornControl.EXT_IN", "horn.HORN", "reverser.REVERSER",
            "windowLeft.EXT_IN", "brakeCutout.EXT_IN", "compressor.ACTIVATION_SIGNAL_EXT_IN",
        },
        interactablePorts: new[] { "throttle.EXT_IN", "hornControl.EXT_IN", "windowLeft.EXT_IN", "brakeCutout.EXT_IN", "engine.RPM" },
        hostKeptPorts: new[] { "compressor.ACTIVATION_SIGNAL_EXT_IN" });

    [Test]
    public void Classify_SplitsPlayerFromSimulationControls()
    {
        ControlPortWriters<string> ports = DE6();
        Assert.That(ports.Classify("throttle.EXT_IN"), Is.EqualTo(ControlPortWriter.Player));
        Assert.That(ports.Classify("windowLeft.EXT_IN"), Is.EqualTo(ControlPortWriter.Player));
        Assert.That(ports.Classify("horn.HORN"), Is.EqualTo(ControlPortWriter.Simulation));
        Assert.That(ports.Classify("reverser.REVERSER"), Is.EqualTo(ControlPortWriter.Simulation));
        Assert.That(ports.Classify("compressor.ACTIVATION_SIGNAL_EXT_IN"), Is.EqualTo(ControlPortWriter.HostKept));
        Assert.That(ports.Classify("engine.RPM"), Is.EqualTo(ControlPortWriter.NotAControl));
    }

    [Test]
    public void InteractableOnANonControlPort_IsNotAPlayerPort()
    {
        ControlPortWriters<string> ports = DE6();
        Assert.That(ports.PlayerPortCount, Is.EqualTo(4));
        Assert.That(ports.ClientReports("engine.RPM"), Is.False);
    }

    [Test]
    public void HostKeptPort_StaysTheHostsEvenWithAnInteractable()
    {
        ControlPortWriters<string> ports = new(new[] { "c.SIGNAL" }, new[] { "c.SIGNAL" }, new[] { "c.SIGNAL" });
        Assert.That(ports.Classify("c.SIGNAL"), Is.EqualTo(ControlPortWriter.HostKept));
        Assert.That(ports.AddPlayerPort("c.SIGNAL"), Is.False);
        Assert.That(ports.HostAccepts("c.SIGNAL", senderHoldsLease: true), Is.False);
    }

    [Test]
    public void RuntimeHookedControl_BecomesAPlayerPort()
    {
        ControlPortWriters<string> ports = DE6();
        Assert.That(ports.AddPlayerPort("reverser.REVERSER"), Is.True);
        Assert.That(ports.Classify("reverser.REVERSER"), Is.EqualTo(ControlPortWriter.Player));
        Assert.That(ports.AddPlayerPort("engine.RPM"), Is.False);
    }

    [Test]
    public void HostAccepts_PlayerPortsAndLeasedControlsOnly()
    {
        ControlPortWriters<string> ports = DE6();
        Assert.That(ports.HostAccepts("windowLeft.EXT_IN", senderHoldsLease: false), Is.True);
        Assert.That(ports.HostAccepts("horn.HORN", senderHoldsLease: false), Is.False);
        Assert.That(ports.HostAccepts("horn.HORN", senderHoldsLease: true), Is.True);
        Assert.That(ports.HostAccepts("compressor.ACTIVATION_SIGNAL_EXT_IN", senderHoldsLease: false), Is.False);
        Assert.That(ports.HostAccepts("engine.RPM", senderHoldsLease: true), Is.False);
    }

    [Test]
    public void Unleased_SendsAPlayersChangeToTheirControl()
    {
        Assert.That(DE6().Unleased("windowLeft.EXT_IN", false, true, 1f, 0f, 10f, Tolerance, Settle),
            Is.EqualTo(UnleasedChange.Send));
    }

    [Test]
    public void Unleased_NeverSendsSimulationControls()
    {
        // Playtest 2: a client's 0 for the compressor signal overwrote the host's 1.
        ControlPortWriters<string> ports = DE6();
        Assert.That(ports.Unleased("compressor.ACTIVATION_SIGNAL_EXT_IN", false, true, 0f, 1f, 10f, Tolerance, Settle),
            Is.EqualTo(UnleasedChange.No));
        Assert.That(ports.Unleased("horn.HORN", false, true, 1f, 0f, 10f, Tolerance, Settle),
            Is.EqualTo(UnleasedChange.No));
        Assert.That(ports.Unleased("reverser.REVERSER", false, true, 0f, 1f, null, Tolerance, Settle),
            Is.EqualTo(UnleasedChange.No));
    }

    [Test]
    public void Unleased_FeedbackWithinToleranceIsNotAChange()
    {
        Assert.That(DE6().Unleased("throttle.EXT_IN", false, true, 0.51f, 0.5f, 10f, Tolerance, Settle),
            Is.EqualTo(UnleasedChange.No));
    }

    [Test]
    public void Unleased_WaitsWhileTheHostsValueSettles()
    {
        ControlPortWriters<string> ports = DE6();
        Assert.That(ports.Unleased("throttle.EXT_IN", false, true, 1f, 0f, 0.5f, Tolerance, Settle),
            Is.EqualTo(UnleasedChange.Settling));
        Assert.That(ports.Unleased("throttle.EXT_IN", false, true, 1f, 0f, null, Tolerance, Settle),
            Is.EqualTo(UnleasedChange.Send));
    }

    [Test]
    public void Unleased_NotWhenNeverHeardFromTheHost()
    {
        Assert.That(DE6().Unleased("throttle.EXT_IN", false, true, 1f, null, 10f, Tolerance, Settle), Is.EqualTo(UnleasedChange.No));
    }

    [Test]
    public void Unleased_PutsBackAChangeTheSenderCantMake()
    {
        // #159: a client left behind by its rolling loco set its valves; dropping the change left
        // them open on that game only, for good.
        ControlPortWriters<string> ports = DE6();
        Assert.That(ports.Unleased("throttle.EXT_IN", false, false, 1f, 0f, 10f, Tolerance, Settle), Is.EqualTo(UnleasedChange.PutBack));
        Assert.That(ports.Unleased("throttle.EXT_IN", true, true, 1f, 0f, 10f, Tolerance, Settle), Is.EqualTo(UnleasedChange.PutBack));
        // Even while the host's value settles: it's the value the control is heading for.
        Assert.That(ports.Unleased("throttle.EXT_IN", false, false, 1f, 0f, 0.5f, Tolerance, Settle), Is.EqualTo(UnleasedChange.PutBack));
    }

    [Test]
    public void Unleased_NothingToPutBackWhenItMatchesTheHost()
    {
        ControlPortWriters<string> ports = DE6();
        Assert.That(ports.Unleased("throttle.EXT_IN", false, false, 0.51f, 0.5f, 10f, Tolerance, Settle), Is.EqualTo(UnleasedChange.No));
        Assert.That(ports.Unleased("throttle.EXT_IN", true, true, 0.5f, 0.5f, 10f, Tolerance, Settle), Is.EqualTo(UnleasedChange.No));
        // Not a control players drive: never sent, never put back.
        Assert.That(ports.Unleased("horn.HORN", false, false, 1f, 0f, 10f, Tolerance, Settle), Is.EqualTo(UnleasedChange.No));
    }
}
