using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using HarmonyLib;
using Multiplayer.Client.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// Other players' gadget tools, seen and heard (gadgets.md, "Tool use other players see", PR 11). This
/// player's game reports its own tool (any gadget interactor: drill, soldering iron, tape, wiring tool,
/// hole filler, hammer) starting and stopping work, from vanilla's <c>GadgetInteractor.IsPressed</c>, and
/// again every second while it works (the host's player through its local session, like its other requests). The games
/// that hear of another player's tool working run its working effects on that player's tool: the drill's
/// motor and spinning head, the soldering iron's sparks and sound. A tool not heard of for 2.5 s (let go,
/// the player gone) stops. The one-shot sounds (placing, removing) come with the change log's entries.
/// </summary>
[HarmonyPatch]
internal static class GadgetToolPresence
{
    private const float Heartbeat = 1f;
    private const float Expiry = 2.5f;

    // This player's own working tools, and when each was last reported.
    private static readonly Dictionary<GadgetInteractor, float> ownWorking = new();
    // Other players' tools that work, by item NetId, and when they were last heard of.
    private static readonly Dictionary<ushort, float> othersWorking = new();
    private static bool subscribed;
    private static bool showing;

    /// <summary>Whether this game shows another player's tool working (scenarios).</summary>
    internal static bool ShowsWorking(ushort toolNetId) =>
        othersWorking.TryGetValue(toolNetId, out float at) && Time.realtimeSinceStartup - at < Expiry;

    internal static void Receive(ClientboundGadgetToolUsePacket packet)
    {
        if (packet.Working)
            othersWorking[packet.ToolItemNetId] = Time.realtimeSinceStartup;
        else
            othersWorking.Remove(packet.ToolItemNetId);
        Subscribe();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GadgetInteractor), nameof(GadgetInteractor.IsPressed), MethodType.Setter)]
    private static void Pressed(GadgetInteractor __instance, bool value)
    {
        if (showing || !NetworkRole.HasLocalPlayer || __instance == null || __instance.GetComponent<ItemBase>() is not { } item ||
            !item.IsGrabbed())
            return;
        bool was = ownWorking.ContainsKey(__instance);
        if (value == was)
            return;
        if (value)
            ownWorking[__instance] = Time.realtimeSinceStartup;
        else
            ownWorking.Remove(__instance);
        Report(__instance, value);
        Subscribe();
    }

    private static void Report(GadgetInteractor tool, bool working)
    {
        ushort netId = tool.GetComponent<NetworkedItem>()?.NetId ?? 0;
        if (netId == 0)
            return;
        var packet = new ServerboundGadgetToolUsePacket { ToolItemNetId = netId, Working = working };
        if (NetworkLifecycle.Instance?.LocalSession is LocalPlayerSession host)
            host.SendGadgetToolUse(packet);
        else
            NetworkClient.Instance?.SendGadgetToolUse(packet);
    }

    private static void Subscribe()
    {
        if (subscribed || NetworkLifecycle.Instance == null)
            return;
        NetworkLifecycle.Instance.OnTick += Tick;
        subscribed = true;
    }

    private static void Tick(uint _)
    {
        float now = Time.realtimeSinceStartup;
        foreach (KeyValuePair<GadgetInteractor, float> pair in ownWorking.ToList())
        {
            GadgetInteractor tool = pair.Key;
            if (tool == null || tool.GetComponent<ItemBase>() is not { } item || !item.IsGrabbed() || !tool.IsPressed)
            {
                ownWorking.Remove(tool);
                if (tool != null)
                    Report(tool, false);
                continue;
            }
            if (now - pair.Value >= Heartbeat)
            {
                ownWorking[tool] = now;
                Report(tool, true);
            }
        }
        foreach (ushort gone in othersWorking.Where(pair => now - pair.Value >= Expiry).Select(pair => pair.Key).ToList())
            othersWorking.Remove(gone);
        ShowSoldering();
    }

    private static bool Shown(Component tool) =>
        othersWorking.Count > 0 && tool != null && tool.GetComponent<NetworkedItem>() is { NetId: not 0 } item && ShowsWorking(item.NetId);

    // The drill: its motor and head as if pressed on a target, for the other player holding it.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DrillEffects), "Update")]
    private static void DrillStart(DrillEffects __instance, out (bool pressed, bool valid)? __state)
    {
        __state = null;
        if (!Shown(__instance) || __instance.drill == null || __instance.drill.IsPressed)
            return;
        __state = (__instance.drill.IsPressed, __instance.drill.TargetIsValid);
        showing = true;
        __instance.drill.IsPressed = true;
        __instance.drill.TargetIsValid = true;
        showing = false;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(DrillEffects), "Update")]
    private static void DrillEnd(DrillEffects __instance, (bool pressed, bool valid)? __state)
    {
        if (__state is not { } before || __instance.drill == null)
            return;
        showing = true;
        __instance.drill.IsPressed = before.pressed;
        __instance.drill.TargetIsValid = before.valid;
        showing = false;
    }

    // The soldering iron: sparks and its sound, for the other player holding it. Its own update runs only
    // in its holder's game, so the tick turns them on and off here.
    private static readonly HashSet<GadgetSolderingTool> sparking = new();

    private static void ShowSoldering()
    {
        foreach (ushort netId in othersWorking.Keys)
        {
            if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null ||
                item.GetComponentInChildren<GadgetSolderingTool>(true) is not { } iron)
                continue;
            iron.SetParticles(on: true);
            if (iron.soundSoldering != null && !iron.soundSoldering.isPlaying)
                iron.soundSoldering.Play();
            sparking.Add(iron);
        }
        foreach (GadgetSolderingTool iron in sparking.Where(i => i == null || !Shown(i)).ToList())
        {
            sparking.Remove(iron);
            if (iron == null)
                continue;
            iron.SetParticles(on: false);
            iron.soundSoldering?.Stop();
        }
    }
}
