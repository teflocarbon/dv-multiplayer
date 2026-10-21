using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;

namespace Multiplayer.Shared.Gadgets;

/// <summary>A button on an installed gadget (gadgets.md, "Gadget state"): commands have no state of
/// their own, only an effect on the gadget's running state.</summary>
public enum GadgetCommand : byte
{
    AtsAcknowledge = 1,
    DistanceStart = 2,
    DistanceAcknowledge = 3,
    DefectSnooze = 4
}

public static class GadgetCommands
{
    /// <summary>Whether <paramref name="command"/> is a button <paramref name="gadget"/> has.</summary>
    public static bool Fits(GadgetBase gadget, GadgetCommand command) => command switch
    {
        GadgetCommand.AtsAcknowledge => gadget is GadgetATS,
        GadgetCommand.DistanceStart or GadgetCommand.DistanceAcknowledge => gadget is GadgetRoadrunner,
        GadgetCommand.DefectSnooze => GadgetRunningState.DefectDetector(gadget) != null,
        _ => false
    };

    /// <summary>
    /// What the button does, as vanilla's own button does it, in the running state's apply scope.
    /// The snooze takes whether there is a defect from <paramref name="hasDefect"/>: the detector's
    /// LOD works it out only where it's loaded, so the server answers it from its own trains.
    /// </summary>
    public static void Run(GadgetBase gadget, GadgetCommand command, bool hasDefect)
    {
        switch (command)
        {
            case GadgetCommand.AtsAcknowledge:
                var ats = (GadgetATS)gadget;
                ats.currentTimer = ats.CurrentRegimeTimerLength;
                break;
            case GadgetCommand.DistanceStart:
                ((GadgetRoadrunner)gadget).StartMeasure();
                break;
            case GadgetCommand.DistanceAcknowledge:
                ((GadgetRoadrunner)gadget).Acknowledge();
                break;
            case GadgetCommand.DefectSnooze:
                GadgetRunningState.Apply(gadget, new[] { "snoozed" }, new[] { hasDefect ? 1f : 0f });
                break;
        }
    }
}
