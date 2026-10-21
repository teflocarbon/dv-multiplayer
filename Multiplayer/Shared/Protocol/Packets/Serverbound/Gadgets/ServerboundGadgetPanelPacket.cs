namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player used a control panel's joystick or button (gadgets.md, "Logic that writes the train"): a
/// <see cref="Multiplayer.Shared.Gadgets.GadgetPanelInput"/> on the car the panel is installed in. The
/// server applies it as the panel would, under the same lease rules as a cab lever, and the car's
/// control sync shows the result.
/// </summary>
public class ServerboundGadgetPanelPacket
{
    public ushort ItemNetId { get; set; }
    public byte Input { get; set; }
    /// <summary>For a joystick move: the cab control it moves (InteriorControlsManager.ControlType).</summary>
    public byte Control { get; set; }
    /// <summary>A joystick move's direction (its sign), or the horn's or a button's value.</summary>
    public float Value { get; set; }
}
