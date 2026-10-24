using System.Collections;
using DV.Items;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
/// Ammo in a tool's magazine: a cassette in a boombox, a paint can in a sprayer, a spool in a
/// soldering tool (docs/design/items/containers.md, "Ammo in a magazine"). Vanilla's magazine makes
/// its ammo inactive and keeps it in its slot; the ammo's home is the tool and the slot, the same on
/// every game, and the server loads it into its copy and every other game's.
/// </summary>
public partial class NetworkedItem
{
    // The tool it was last in, kept when vanilla empties the magazine as its tool goes.
    private ushort magazineToolNetId;
    private int magazineSlot = -1;

    /// <summary>The tool whose magazine it's in, while it's in one.</summary>
    internal ushort MagazineToolNetId => magazineToolNetId;

    /// <summary>Vanilla loaded it into a magazine or took it out (MagazinePatch): look again.</summary>
    internal void NoteMagazineChanged(string reason) => MarkLocalStateDirty(reason);

    /// <summary>The networked tool whose magazine holds this item, and the slot, if any.</summary>
    internal bool TryGetMagazine(out NetworkedItem tool, out ItemMagazine magazine, out int slot)
    {
        tool = null;
        slot = -1;
        magazine = Item?.InContainer as ItemMagazine;
        if (magazine == null)
            return false;
        tool = magazine.GetComponentInParent<NetworkedItem>();
        if (tool == null || tool == this || tool.NetId == 0)
            return false;
        for (int i = 0; i < magazine.Capacity; i++)
            if (magazine[i] == gameObject)
            {
                slot = i;
                break;
            }
        return slot >= 0;
    }

    // The observation (GetItemState): in a magazine, or held there while its tool is gone. Vanilla
    // empties a magazine as its tool is destroyed (out of interest, put in a box); where the ammo
    // goes then is the server's to say, so it isn't reported dropped meanwhile.
    private bool ObservedInMagazine()
    {
        if (TryGetMagazine(out NetworkedItem tool, out _, out int slot))
        {
            magazineToolNetId = tool.NetId;
            magazineSlot = slot;
            return true;
        }
        return lastState == ItemState.InMagazine &&
               !(TryGet(magazineToolNetId, out NetworkedItem lastTool) && lastTool != null);
    }

    /// <summary>The magazine slot as the wire projection keeps it: the tool in the car field, the slot in the hand's.</summary>
    private (ushort tool, byte slot) MagazineProjection(ItemState state) =>
        state == ItemState.InMagazine ? (magazineToolNetId, (byte)Mathf.Clamp(magazineSlot, 0, byte.MaxValue)) : ((ushort)0, (byte)0);

    private void WriteMagazine(ItemUpdate update)
    {
        bool inMagazine = update.ItemState == ItemState.InMagazine;
        update.MagazineNetId = inMagazine ? magazineToolNetId : (ushort)0;
        update.MagazineSlot = inMagazine ? magazineSlot : -1;
    }

    // Out of its magazine when its home is anywhere else. Vanilla's own removal (an eject) has done
    // this already; the server's word (a drop, a pickup, Lost and Found) hasn't.
    private void LeaveMagazine(ItemState targetState)
    {
        if (targetState != ItemState.InMagazine)
            LeaveMagazine();
    }

    /// <summary>Out of the magazine it's in, if any: the server put it somewhere else (Lost and Found).</summary>
    internal void LeaveMagazine()
    {
        if (Item?.InContainer is not ItemMagazine magazine)
            return;
        magazine.RemoveItem(gameObject, false, false);
        magazineToolNetId = 0;
        magazineSlot = -1;
    }

    private void HandleMagazineState(ItemUpdate snapshot)
    {
        if (TryGetMagazine(out NetworkedItem current, out _, out int slot) &&
            current.NetId == snapshot.MagazineNetId && slot == snapshot.MagazineSlot)
            return;
        magazineToolNetId = snapshot.MagazineNetId;
        magazineSlot = snapshot.MagazineSlot;
        if (TryGet(snapshot.MagazineNetId, out NetworkedItem tool) && tool != null)
        {
            LoadInto(tool, snapshot.MagazineSlot);
            return;
        }
        // Its tool's Create hasn't come yet (both came into interest together).
        DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "missing-magazine-tool", $"toolNetId={snapshot.MagazineNetId}");
        NetworkedItemManager.Instance?.StartCoroutine(LoadWhenToolArrives(snapshot.MagazineNetId, snapshot.MagazineSlot));
    }

    private IEnumerator LoadWhenToolArrives(ushort toolNetId, int slot)
    {
        for (float waited = 0; waited < 10f; waited += Time.unscaledDeltaTime)
        {
            yield return null;
            if (this == null || magazineToolNetId != toolNetId || lastState != ItemState.InMagazine)
                yield break;
            if (TryGet(toolNetId, out NetworkedItem tool) && tool != null)
            {
                DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "missing-magazine-tool");
                LoadInto(tool, slot);
                yield break;
            }
        }
        Multiplayer.LogWarning($"Item {name} ({NetId}) is in tool {toolNetId}'s magazine, which never arrived");
    }

    private void LoadInto(NetworkedItem tool, int slot)
    {
        ItemMagazine magazine = tool.GetComponentInChildren<ItemMagazine>(true);
        if (magazine == null || slot < 0 || slot >= magazine.Capacity)
        {
            Multiplayer.LogWarning($"Item {name} ({NetId}) can't go into slot {slot} of {tool.name} ({tool.NetId}): " +
                                   (magazine == null ? "it has no magazine" : $"it has {magazine.Capacity} slot(s)"));
            return;
        }
        DetachForWorldState(ItemState.InMagazine);
        Item.InContainer?.RemoveItem(gameObject, false, false);
        // The server's word goes: whatever this game had in the slot comes out first.
        GameObject there = magazine[slot];
        if (there != null && there != gameObject)
            magazine.RemoveItem(slot, true, true);
        if (!magazine.AddItem(gameObject, slot))
            Multiplayer.LogWarning($"Item {name} ({NetId}): {tool.name}'s magazine refused it in slot {slot}");
    }
}
