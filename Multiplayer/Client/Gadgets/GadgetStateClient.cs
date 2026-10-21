using DV.Customization.Gadgets;
using Multiplayer.Core.Gadgets;
using Multiplayer.Client.Session;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// A remote client's side of gadget settings (gadgets.md, "Gadget state"), for the gadgets the server
/// sent: a change this player makes (a knob, a switch, the HUD) shows at once and goes to the server
/// as a request; the server's state replaces what this game shows unless this player's own newer
/// change is still on its way (GadgetSettingEcho). Changes are found by reading the settings each
/// tick, the way the server's watch does, so every path to a setter is covered.
/// </summary>
internal static class GadgetStateClient
{
    private const float SendIntervalSeconds = 0.1f;
    private const float PendingTimeoutSeconds = 5f;

    private sealed class Tracked
    {
        // What this game last told the server, or was told by it.
        public Dictionary<string, float> Shown;
        public uint Pending;
        public float PendingSince;
        public float LastSent = float.NegativeInfinity;
    }

    private static readonly Dictionary<GadgetBase, Tracked> tracked = new();
    private static uint nextOperation;
    private static bool subscribed;

    internal static void Reset() => tracked.Clear();

    /// <summary>A gadget the server sent: its settings as it arrived are the server's.</summary>
    internal static void Track(GadgetBase gadget)
    {
        if (!GadgetSettings.Has(gadget))
            return;
        tracked[gadget] = new Tracked { Shown = GadgetSettings.Capture(gadget) };
        if (!subscribed && NetworkLifecycle.Instance != null)
        {
            NetworkLifecycle.Instance.OnTick += Tick;
            subscribed = true;
        }
    }

    internal static void Receive(ClientboundGadgetStatePacket packet)
    {
        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) || item == null ||
            item.GetComponent<GadgetItem>()?.Gadget is not { IsLinked: true } gadget ||
            !tracked.TryGetValue(gadget, out Tracked entry))
            return;
        GadgetSettingEchoDecision decision = GadgetSettingEcho.Decide(NetworkClient.Instance?.PlayerId ?? 0, entry.Pending,
            packet.PlayerId, packet.OperationId, packet.Refused);
        if (decision.ClearsPending)
            entry.Pending = 0;
        if (decision.Action != GadgetSettingEchoAction.Apply)
            return;
        GadgetSettings.Apply(gadget, packet.Keys, packet.Values);
        // As this game now shows them: a knob's position can round them.
        entry.Shown = GadgetSettings.Capture(gadget);
    }

    private static void Tick(uint _)
    {
        if (!NetworkRole.IsRemoteClient || tracked.Count == 0)
            return;
        float now = Time.realtimeSinceStartup;
        foreach (KeyValuePair<GadgetBase, Tracked> pair in tracked.ToList())
        {
            GadgetBase gadget = pair.Key;
            Tracked entry = pair.Value;
            if (gadget == null || !gadget.IsLinked)
            {
                tracked.Remove(gadget);
                continue;
            }
            if (entry.Pending != 0 && now - entry.PendingSince > PendingTimeoutSeconds)
                entry.Pending = 0;
            if (now - entry.LastSent < SendIntervalSeconds)
                continue;
            Dictionary<string, float> settings = GadgetSettings.Capture(gadget);
            if (!GadgetSettings.Differ(settings, entry.Shown))
                continue;
            ushort itemNetId = gadget.GadgetItem?.GetComponent<NetworkedItem>()?.NetId ?? 0;
            if (itemNetId == 0)
                continue;
            entry.Shown = settings;
            entry.Pending = ++nextOperation;
            entry.PendingSince = entry.LastSent = now;
            NetworkClient.Instance?.SendGadgetSetting(new ServerboundGadgetSettingPacket
            {
                ItemNetId = itemNetId,
                OperationId = entry.Pending,
                Keys = settings.Keys.ToArray(),
                Values = settings.Values.ToArray()
            });
        }
    }
}
