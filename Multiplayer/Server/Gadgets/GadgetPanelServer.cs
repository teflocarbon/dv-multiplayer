using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.HUD;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using HarmonyLib;
using Multiplayer.Server.Session;
using Multiplayer.Server.Trains;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Multiplayer.Server.Gadgets;

/// <summary>
/// Control panels on the server (gadgets.md, "Logic that writes the train"): a player's joystick
/// step or button press (GadgetPanelClientPatches) is applied here as the panel would apply it, on the
/// car it is installed in. It is refused, as a cab lever would be, while another player holds the
/// lever it moves; and as vanilla's panel ignores it, while the panel is unpowered or its rain fault
/// holds (the same roll in every game, GadgetControlPanels). A move with no cab loaded steps by the
/// cab lever's scroll step (GadgetControlWriter). The result reaches every game through the car's
/// control sync. The host's own player at a panel goes through vanilla, under the same rules.
/// </summary>
[HarmonyPatch]
internal static class GadgetPanelServer
{
    internal static void Handle(ServerboundGadgetPanelPacket packet, ServerPlayer player)
    {
        GadgetBase gadget = GadgetStateServer.LinkedGadget(packet.ItemNetId);
        var input = (GadgetPanelInput)packet.Input;
        var type = (InteriorControlsManager.ControlType)packet.Control;
        string refusal = player.LoadingState < PlayerLoadingState.ReadyForCustomizers ? "still loading"
            : gadget is not GadgetControlPanel panel ? "not an installed control panel"
            : float.IsNaN(packet.Value) || float.IsInfinity(packet.Value) ? "not a number"
            : Refusal(panel, input, type, packet.Value, player) ?? GadgetStateServer.TouchRefusal(player, panel, packet.ItemNetId);
        if (refusal != null)
        {
            Multiplayer.LogDebug(() => $"Control panel {input} {type} from {player.Username} on item {packet.ItemNetId} refused: {refusal}");
            return;
        }
        Apply((GadgetControlPanel)gadget, input, type, packet.Value);
    }

    /// <summary>Why the panel won't apply this input for <paramref name="player"/> now, or null if it will.</summary>
    private static string Refusal(GadgetControlPanel panel, GadgetPanelInput input, InteriorControlsManager.ControlType type,
        float value, ServerPlayer player)
    {
        bool released = (input == GadgetPanelInput.Starter || input == GadgetPanelInput.PowerOff) && value == 0f;
        if (!panel.PowerState && !released)
            return "unpowered";
        if (input is GadgetPanelInput.Move or GadgetPanelInput.Horn && GadgetControlPanels.Faulted(panel))
            return "the rain fault holds";
        OverridableBaseControl control = Control(panel, input, type);
        if (control == null)
            return $"no {input} {type} on {panel.TrainCar?.ID}";
        return HeldByAnother(panel, control, player) ? $"another player holds {control.ControlType}" : null;
    }

    private static OverridableBaseControl Control(GadgetControlPanel panel, GadgetPanelInput input, InteriorControlsManager.ControlType type)
    {
        BaseControlsOverrider controls = panel.Controls;
        if (controls == null)
            return null;
        return input switch
        {
            GadgetPanelInput.Move => type == InteriorControlsManager.ControlType.None ? null : controls.GetControl(type),
            GadgetPanelInput.Horn => controls.Horn,
            GadgetPanelInput.Starter => controls.Starter,
            GadgetPanelInput.PowerOff => controls.PowerOff,
            _ => null
        };
    }

    // A lever someone else holds, as a cab lever's lease rules have it.
    private static bool HeldByAnother(GadgetControlPanel panel, OverridableBaseControl control, ServerPlayer player)
    {
        if (string.IsNullOrEmpty(control.portId) || panel.TrainCar == null ||
            !NetworkedTrainCar.TryGetFromTrainCar(panel.TrainCar, out NetworkedTrainCar networked) ||
            networked.Server() is not { } server)
            return false;
        ServerPlayer holder = server.LeaseHolder(NetworkedTrainCar.GetPortNetId(control.portId));
        return holder != null && holder != player;
    }

    private static void Apply(GadgetControlPanel panel, GadgetPanelInput input, InteriorControlsManager.ControlType type, float value)
    {
        OverridableBaseControl control = Control(panel, input, type);
        switch (input)
        {
            case GadgetPanelInput.Move:
                GadgetControlWriter.Stepping(panel, () => control.Move(Mathf.Sign(value)));
                // The joystick for the cab light moves the dash light's indicator too, as vanilla's does.
                if (type == InteriorControlsManager.ControlType.CabLight)
                    GadgetControlWriter.Stepping(panel, () => panel.Controls.GetControl(InteriorControlsManager.ControlType.IndCabLight)?.Move(Mathf.Sign(value)));
                break;
            case GadgetPanelInput.Horn:
                control.Set(Mathf.Clamp01(Mathf.Abs(value)));
                break;
            default:
                control.Set(Mathf.Clamp01(value));
                break;
        }
    }

    // The host's own player at a panel: vanilla's move, refused while another player holds the lever,
    // and stepped as a client's would be when the server has no cab loaded.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetControlPanelLODObject), "UpdateOverridableControlUsingJoystick")]
    private static bool HostJoystick(GadgetControlPanelLODObject __instance, float value, InteriorControlsManager.ControlType type)
    {
        if (!NetworkRole.IsServer || __instance.Base is not { } panel || NetworkServer.Instance?.LocalPlayer is not { } host)
            return true;
        if (!__instance.IsJoystickValueActive(value) || __instance.isInFaultState)
            return false;
        OverridableBaseControl control = panel.Controls?.GetControl(type);
        if (control == null || HeldByAnother(panel, control, host))
            return false;
        GadgetControlWriter.Stepping(panel, () => control.Move(Mathf.Sign(value)));
        return false;
    }
}
