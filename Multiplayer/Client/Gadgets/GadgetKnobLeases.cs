using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using HarmonyLib;
using Multiplayer.Core.Gadgets;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// Gadget knobs, levers and joysticks take leases like cab levers (gadgets.md, "Optimistic"): grabbing
/// one of an installed gadget's controls takes the gadget's lease (GadgetKnobLease) through the
/// cab-control path, on the gadget's car, and letting go gives it back, so two players never fight over
/// one gadget. While another player holds it, this game's controls of that gadget are blocked, as a cab
/// lever is, and the server refuses this player's changes to it from anywhere else (the HUD). A press
/// without a grab (a VR button, a keyboard press) holds the lease for half a second, as a cab button
/// does. Runs in every game with a player, the host's included: its player's grab goes through the
/// server's request queue like anyone's.
/// </summary>
[HarmonyPatch]
internal static class GadgetKnobLeases
{
    private const float UsedLeaseSeconds = 0.5f;
    private const float SettleSeconds = 0.5f;

    private sealed class Hooked
    {
        public GadgetBase Gadget;
        public readonly List<ControlImplBase> Controls = new();
        public readonly Dictionary<ControlImplBase, Action> UsedDelegates = new();
        // The lease this game asked for and hasn't given back: on what car, under what id.
        public ushort HeldCar;
        public uint HeldLease;
        public Coroutine Release;
    }

    private static readonly Dictionary<GadgetBase, Hooked> hooked = new();
    // Leases another player holds, by id, whether or not this game has the gadget's controls loaded.
    private static readonly HashSet<uint> heldByOthers = new();

    internal static void Reset()
    {
        foreach (Hooked entry in hooked.Values.ToList())
            Unhook(entry, release: false);
        hooked.Clear();
        heldByOthers.Clear();
    }

    /// <summary>Whether this game's player holds the gadget's knobs (tests and the status board).</summary>
    internal static bool HoldsLease(GadgetBase gadget) =>
        gadget != null && hooked.TryGetValue(gadget, out Hooked entry) && entry.HeldLease != 0;

    /// <summary>Whether another player holds the gadget's knobs, as this game last heard.</summary>
    internal static bool HeldByAnother(GadgetBase gadget) =>
        LeaseId(gadget, out uint lease, out _) && heldByOthers.Contains(lease);

    // Linking loads the LOD if the car is close (SetLODState, which also runs when nothing changes);
    // unlinking unloads it first.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.SetLODState))]
    private static void LodStateSet(Customization.CustomizerBase __instance, bool loaded)
    {
        if (__instance is not GadgetBase gadget || !NetworkRole.HasLocalPlayer)
            return;
        if (loaded && gadget.IsLinked && gadget.IsOnTrainCar)
            CoroutineManager.Instance.StartCoroutine(HookNextFrame(gadget));
        else if (hooked.TryGetValue(gadget, out Hooked entry))
            Unhook(entry, release: true);
    }

    // A control is made by its spec's Awake as the LOD activates; vanilla's own LODs wire theirs a frame
    // later (GadgetSwitchLOD.Initialize).
    private static IEnumerator HookNextFrame(GadgetBase gadget)
    {
        yield return null;
        if (gadget == null || !gadget.IsLinked || !gadget.IsLODLoaded || hooked.ContainsKey(gadget))
            yield break;
        Hooked entry = new() { Gadget = gadget };
        foreach (CustomizerLODObject lod in gadget.LODObjects)
        {
            if (lod == null)
                continue;
            foreach (ControlImplBase control in lod.GetComponentsInChildren<ControlImplBase>(true))
            {
                control.Grabbed += Grabbed;
                control.Ungrabbed += Ungrabbed;
                void Used() => ControlUsed(control);
                entry.UsedDelegates[control] = Used;
                control.Used += Used;
                entry.Controls.Add(control);
            }
        }
        if (entry.Controls.Count == 0)
            yield break;
        hooked[gadget] = entry;
        if (LeaseId(gadget, out uint lease, out _) && heldByOthers.Contains(lease))
            Block(entry, true);
    }

    private static void Unhook(Hooked entry, bool release)
    {
        if (release)
            GiveBack(entry);
        else if (entry.Release != null)
            CoroutineManager.Instance.StopCoroutine(entry.Release);
        foreach (ControlImplBase control in entry.Controls)
        {
            if (control == null)
                continue;
            control.Grabbed -= Grabbed;
            control.Ungrabbed -= Ungrabbed;
            if (entry.UsedDelegates.TryGetValue(control, out Action used))
                control.Used -= used;
        }
        if (entry.Gadget != null)
            hooked.Remove(entry.Gadget);
        else
            foreach (GadgetBase gone in hooked.Where(pair => pair.Value == entry).Select(pair => pair.Key).ToList())
                hooked.Remove(gone);
    }

    private static Hooked EntryOf(ControlImplBase control) =>
        hooked.Values.FirstOrDefault(entry => entry.Controls.Contains(control));

    private static void Grabbed(ControlImplBase control)
    {
        Hooked entry = EntryOf(control);
        if (entry == null || !Leased(entry.Gadget))
            return;
        if (entry.Release != null)
        {
            CoroutineManager.Instance.StopCoroutine(entry.Release);
            entry.Release = null;
        }
        if (!LeaseId(entry.Gadget, out uint lease, out ushort car))
            return;
        if (heldByOthers.Contains(lease))
        {
            control.ForceEndInteraction();
            return;
        }
        if (entry.HeldLease != 0)
            return;
        entry.HeldLease = lease;
        entry.HeldCar = car;
        TrainRoles.Client?.SendTrainControlAuthorityRequest(car, lease, true);
    }

    // A press without a grab: lease it, and give it back once the change has gone out.
    private static void ControlUsed(ControlImplBase control)
    {
        if (control == null || control.IsGrabbed())
            return;
        Hooked entry = EntryOf(control);
        if (entry == null || !Leased(entry.Gadget))
            return;
        Grabbed(control);
        if (entry.HeldLease == 0)
            return;
        if (entry.Release != null)
            CoroutineManager.Instance.StopCoroutine(entry.Release);
        entry.Release = CoroutineManager.Instance.StartCoroutine(ReleaseAfter(entry, UsedLeaseSeconds, settle: false));
    }

    private static void Ungrabbed(ControlImplBase control)
    {
        Hooked entry = EntryOf(control);
        if (entry == null || entry.HeldLease == 0 || entry.Controls.Any(c => c != null && c.IsGrabbed()))
            return;
        if (entry.Release != null)
            CoroutineManager.Instance.StopCoroutine(entry.Release);
        // A sprung lever or a knob still turning: its last moves are the player's too.
        bool sprung = control != null && (control.GetComponent<HingeJoint>() is { useSpring: true } ||
                                           control.GetComponent<ConfigurableJoint>() != null);
        entry.Release = CoroutineManager.Instance.StartCoroutine(ReleaseAfter(entry, sprung ? SettleSeconds : 0f, sprung));
    }

    private static IEnumerator ReleaseAfter(Hooked entry, float seconds, bool settle)
    {
        if (seconds > 0f)
            yield return new WaitForSecondsRealtime(seconds);
        if (settle)
        {
            float[] Values() => entry.Controls.Select(c => c != null ? c.Value : 0f).ToArray();
            float[] before;
            do
            {
                before = Values();
                yield return new WaitForSecondsRealtime(SettleSeconds);
            } while (!before.SequenceEqual(Values()));
        }
        entry.Release = null;
        if (entry.Controls.Any(c => c != null && c.IsGrabbed()))
            yield break;
        GiveBack(entry);
    }

    private static void GiveBack(Hooked entry)
    {
        if (entry.Release != null)
        {
            CoroutineManager.Instance.StopCoroutine(entry.Release);
            entry.Release = null;
        }
        if (entry.HeldLease == 0)
            return;
        // The last change goes before the release: after it, another player's grab may come first.
        if (NetworkRole.IsRemoteClient && entry.Gadget != null)
            GadgetStateClient.SendNow(entry.Gadget);
        TrainRoles.Client?.SendTrainControlAuthorityRequest(entry.HeldCar, entry.HeldLease, false);
        entry.HeldLease = 0;
        entry.HeldCar = 0;
    }

    /// <summary>
    /// A lease update from the server, for any control on any car (NetworkedTrainCarClient forwards
    /// them all): those for a gadget block or free its controls here.
    /// </summary>
    internal static void ReceiveAuthorityUpdate(uint lease, ControlAuthorityState state)
    {
        bool block = state == ControlAuthorityState.Blocked || state == ControlAuthorityState.Denied;
        if (block)
            heldByOthers.Add(lease);
        else
            heldByOthers.Remove(lease);
        foreach (Hooked entry in hooked.Values.ToList())
        {
            if (!LeaseId(entry.Gadget, out uint id, out _) || id != lease)
                continue;
            // Refused, or taken from this game's player: it isn't theirs any more.
            if (block)
            {
                if (entry.Release != null)
                    CoroutineManager.Instance.StopCoroutine(entry.Release);
                entry.Release = null;
                entry.HeldLease = 0;
                entry.HeldCar = 0;
            }
            Block(entry, block);
        }
    }

    private static void Block(Hooked entry, bool block)
    {
        foreach (ControlImplBase control in entry.Controls)
        {
            if (control == null)
                continue;
            if (block)
                control.ForceEndInteraction();
            control.BlockControl(block);
            control.InteractionAllowed = !block;
        }
    }

    // The server runs this gadget, so its knobs are leased there: every gadget on the host, and on a
    // remote client the ones the server sent (one it installed itself isn't on the server until 3a).
    private static bool Leased(GadgetBase gadget) =>
        gadget != null && gadget.IsLinked && (NetworkRole.IsServer || GadgetJoinApplier.IsServerSent(gadget));

    private static bool LeaseId(GadgetBase gadget, out uint lease, out ushort carNetId)
    {
        lease = 0;
        carNetId = 0;
        if (gadget == null || gadget.Custom is not TrainCarCustomization { TrainCar: { } car } ||
            !NetworkedTrainCar.TryGetNetId(car, out carNetId) ||
            gadget.GadgetItem?.GetComponent<NetworkedItem>() is not { NetId: not 0 } item)
            return false;
        lease = GadgetKnobLease.IdOf(item.NetId);
        return true;
    }
}
