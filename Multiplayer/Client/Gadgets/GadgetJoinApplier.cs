using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Trains;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// Puts a joining player's installed gadgets on their cars (gadgets.md, "Late joiners") the way
/// vanilla loads a save (StartingItemsController): every gadget's item loads its saved state, which
/// places it and restores its UID, soldering, wires, mount points and settings; then every item's
/// after-load pass binds mounts. Wires resolve in any order because both ends save them. The
/// server's UID is set before each placement, so this game never allocates one of its own.
/// </summary>
internal static class GadgetJoinApplier
{
    private const float ItemWaitSeconds = 10f;

    /// <summary>
    /// The gadgets this game has from the server. Their train-driving logic is the server's
    /// (GadgetServerOnlyPatches); a gadget this game installed itself, which the server hasn't heard
    /// of yet, keeps vanilla's.
    /// </summary>
    internal static readonly HashSet<GadgetBase> ServerSent = new();

    /// <summary>While a join snapshot is being placed: what comes for its gadgets meanwhile waits.</summary>
    internal static bool Applying { get; private set; }

    internal static bool IsServerSent(GadgetBase gadget) => gadget != null && ServerSent.Contains(gadget);

    internal static IEnumerator Apply(ClientboundGadgetJoinSnapshotPacket packet, Action done)
    {
        // A rejoin's snapshot replaces what the last one sent.
        ServerSent.Clear();
        GadgetStateClient.Reset();
        GadgetRunningClient.Reset();
        GadgetKnobLeases.Reset();
        Applying = true;
        List<(TrainCar car, InstalledGadgetData data)> wanted = new();
        foreach (GadgetAnchorSnapshotData anchor in packet.Anchors ?? [])
        {
            if (!NetworkedTrainCar.TryGet(anchor.CarNetId, out TrainCar car) || car == null)
            {
                Multiplayer.LogWarning($"Installed gadgets on car {anchor.CarNetId}, which this game doesn't have: skipped");
                continue;
            }
            wanted.AddRange((anchor.Gadgets ?? []).Select(gadget => (car, gadget)));
        }

        // Their items' Creates came just before, on the same channel; give them a moment to apply.
        float deadline = Time.realtimeSinceStartup + ItemWaitSeconds;
        while (wanted.Any(w => !NetworkedItem.TryGet(w.data.ItemNetId, out NetworkedItem item) || item == null) &&
               Time.realtimeSinceStartup < deadline)
            yield return null;

        List<ItemSaveData> placed = new();
        foreach ((TrainCar car, InstalledGadgetData data) in wanted)
        {
            if (TryPlace(car, data, out ItemSaveData saveData))
                placed.Add(saveData);
        }
        foreach (ItemSaveData saveData in placed)
        {
            try
            {
                saveData.PostLoadItemData();
            }
            catch (Exception exception)
            {
                Multiplayer.LogException($"Installed gadget {saveData.name}: after-load failed", exception);
            }
        }
        // After the after-load pass, which can still move a setting (a knob binding its LOD).
        foreach (GadgetBase gadget in ServerSent)
            GadgetStateClient.Track(gadget);
        Applying = false;
        GadgetRunningClient.ApplyEarly();
        Multiplayer.Log($"Installed {placed.Count} of {wanted.Count} gadgets from the server");
        done?.Invoke();
    }

    private static bool TryPlace(TrainCar car, InstalledGadgetData data, out ItemSaveData saveData)
    {
        saveData = null;
        if (!NetworkedItem.TryGet(data.ItemNetId, out NetworkedItem item) || item == null)
        {
            Multiplayer.LogWarning($"Installed gadget item {data.ItemNetId} on {car.ID} never arrived: skipped");
            return false;
        }
        GadgetItem gadgetItem = item.GetComponent<GadgetItem>();
        saveData = item.GetComponent<ItemSaveData>();
        if (gadgetItem?.Gadget == null || saveData == null)
        {
            Multiplayer.LogWarning($"Item {data.ItemNetId} ({item.name}) isn't a gadget: skipped");
            return false;
        }
        // Already there (a rejoin without a reload): the server's all the same.
        if (gadgetItem.Gadget.IsLinked)
        {
            ServerSent.Add(gadgetItem.Gadget);
            return false;
        }
        try
        {
            JObject state = JObject.Parse(data.State);
            // Loading a gadget whose car is missing sends it to vanilla's Lost and Found.
            if ((string)state["placedOn"] != car.CarGUID)
            {
                Multiplayer.LogWarning($"Installed gadget {item.name} ({data.ItemNetId}) is on '{state["placedOn"]}', " +
                                       $"not {car.ID}'s {car.CarGUID}: skipped");
                return false;
            }
            // Vanilla sets this in Start, which an inactive item never runs; Place needs it.
            gadgetItem.Item ??= item.GetComponent<ItemBase>();
            // Before Place links it: OnAfterLinked allocates a UID only when it has none.
            gadgetItem.Gadget.AssignUID(data.Uid);
            saveData.LoadItemData(state);
            ServerSent.Add(gadgetItem.Gadget);
            return gadgetItem.Gadget.IsLinked;
        }
        catch (Exception exception)
        {
            Multiplayer.LogException($"Installed gadget {item.name} ({data.ItemNetId}) on {car.ID} failed to load", exception);
            return false;
        }
    }
}
