using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Protocol.Data.Items;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.Ledger;

/// <summary>
/// The Release repair's strays and copies (D7; docs/design/items/ledger.md, "The invariant check"):
/// what the check finds that isn't the ledger's item in the ledger's home. An owned one goes to its
/// owner's Lost and Found, an unowned one into the world.
/// </summary>
public static partial class ServerLedger
{
    // Off a coupler, a snap point or a tool's magazine as a retrieval takes it (OI-LIST-14): into its
    // owner's Lost and Found, then on to a slot if its home is one.
    private static void TakeOff(LiveItems.LiveItem record, Home live, Home want, LedgerCause follow)
    {
        Guid id = record.PersistentItemId;
        PlayerItems.WithObject(record.NetId, item =>
        {
            if (item == null || !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem now) || now.PersistentItemId != id)
                return;
            ledger.Repair(id, HomeOf(now), follow);
            string refused = now.Owner == Guid.Empty ? "item-has-no-owner" : string.Empty;
            if (refused.Length > 0 || !ToLostAndFound(item, now, out refused))
            {
                Multiplayer.LogWarning($"Invariant repair: {now.PrefabName} ({id:D}) couldn't be taken off {live} " +
                                       $"({refused}); it stays, and the ledger follows");
                return;
            }
            Multiplayer.Log($"Invariant repair: {now.PrefabName} ({id:D}) was taken off {live} into its owner's Lost and Found");
            if (want.IsWithPlayer && LiveItems.TryGet(item.NetId, out LiveItems.LiveItem lost) && lost.PersistentItemId == id)
                PutHome(lost, want);
        });
    }

    // A live entry with no copy anywhere: made again fresh from its prefab, with the same id, in its
    // owner's Lost and Found, as an item whose state won't load is (D9).
    private static void RemakeLost(Guid id)
    {
        if (!ledger.TryGet(id, out LedgerEntry entry) || !entry.IsLive)
            return;
        if (Copies(id).Count > 0 || LostAndFoundServer.TryGetEntry(id, out _) || LostAndFoundServer.TryGetDataRow(id, out _))
        {
            Multiplayer.LogWarning($"Invariant repair: {entry.Prefab} ({id:D}) was seen with no copy, but one is there now; " +
                                   "it isn't made again");
            return;
        }
        if (entry.Owner.Kind != OwnerKind.Player || !ItemSaveLoad.PrefabKnown(entry.Prefab))
        {
            Multiplayer.LogWarning($"Invariant repair: {entry.Prefab} ({id:D}) has no copy and can't be made again " +
                                   "(no player owns it, or its prefab is unknown)");
            return;
        }
        Multiplayer.Log($"Invariant repair: {entry.Prefab} ({id:D}) has no copy; it is made again in its owner's Lost and Found");
        ItemSaveLoad.MakeLostAgain(id, entry.Prefab, entry.Owner.Id);
    }

    // Two copies or more: the one in the ledger's home keeps the id (else the first live one, put
    // home); each other live copy is a new item, re-minted as a duplicate is (ItemLedger.Register).
    private static void KeepOneCopy(Guid id)
    {
        if (!ledger.TryGet(id, out LedgerEntry entry) || !entry.IsLive)
            return;
        List<LiveItems.LiveItem> copies = Copies(id).ToList();
        LiveItems.LiveItem keep = copies.FirstOrDefault(copy => HomeInvariant.Agrees(entry.Home, HomeOf(copy)));
        bool rowAtHome = keep == null && Observe().Any(seen => seen.Id == id && seen.Source != ObservedSource.LiveObject &&
                                                               HomeInvariant.Agrees(entry.Home, seen.Home));
        if (keep == null && !rowAtHome)
            keep = copies.FirstOrDefault();
        Guid owner = entry.Owner.Kind == OwnerKind.Player ? entry.Owner.Id : Guid.Empty;
        foreach (LiveItems.LiveItem copy in copies.Where(copy => copy != keep))
            Remint(copy, id, owner, "a second copy");
        if (keep != null && !HomeInvariant.Agrees(entry.Home, HomeOf(keep)))
            PutHome(keep, entry.Home);
    }

    // A copy that isn't the item takes a new id, born a duplicate of it (Duplicate, from the first):
    // its owner's, into their Lost and Found; with no owner, into the world.
    private static void Remint(LiveItems.LiveItem copy, Guid from, Guid owner, string why)
    {
        PlayerItems.WithObject(copy.NetId, item =>
        {
            if (item == null || !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem now) || now.PersistentItemId != from)
                return;
            Guid fresh = Guid.NewGuid();
            ItemIdentity identity = item.GetComponent<ItemIdentity>() ?? item.gameObject.AddComponent<ItemIdentity>();
            identity.Value = fresh;
            identity.CopiedFrom = from;
            now.PersistentItemId = fresh;
            netIdByPersistentId[fresh] = item.NetId;
            if (netIdByPersistentId.TryGetValue(from, out ushort held) && held == item.NetId)
                netIdByPersistentId.Remove(from);
            // A copy in Lost and Found keeps its entry, under the new id (else it is saved under none).
            bool inLost = LostAndFoundServer.RekeyItem(item.NetId, fresh);
            Multiplayer.LogError($"item.duplicate-persistent-id: {now.PrefabName} (netId {now.NetId}) was a copy of " +
                                 $"{from:D} ({why}); it is now {fresh:D}");
            // Out of a player's hands first: born where it then lies, it takes no slot the item has.
            ToWorld(item, now);
            if (owner == Guid.Empty)
                return;
            string refused = Own(now, owner, BirthReason.Duplicate, Guid.Empty, RepairDetail);
            if (refused.Length == 0 && !inLost)
                ToLostAndFound(item, now, out refused);
            if (refused.Length > 0)
                Multiplayer.LogWarning($"Invariant repair: the copy {fresh:D} couldn't go to its owner's Lost and Found ({refused})");
        });
    }

    // Two items or more in one holder's slot: the one the ledger has there stays. Another owned one
    // goes to its own home (one whose home is this slot too, to its owner's Lost and Found); an
    // unowned one into the world at its holder's feet.
    private static void OneInSlot(Home slot)
    {
        List<LiveItems.LiveItem> there = LiveItems.GetAll().Where(record => HomeOf(record) == slot).ToList();
        LiveItems.LiveItem keep = there.FirstOrDefault(record =>
                                      ledger.TryGetHome(record.PersistentItemId, out Home home) && home == slot) ??
                                  there.FirstOrDefault();
        foreach (LiveItems.LiveItem record in there.Where(record => record != keep))
        {
            // One repair per item per check: one put home for being elsewhere is put home already.
            if (record.PersistentItemId != Guid.Empty && !repaired.Add(record.PersistentItemId))
                continue;
            if (ledger.TryGetHome(record.PersistentItemId, out Home home))
            {
                PutHome(record, home == slot ? Home.LostAndFound : home);
                continue;
            }
            PlayerItems.WithObject(record.NetId, item =>
            {
                if (item != null && LiveItems.TryGet(item.NetId, out LiveItems.LiveItem now))
                    ToWorld(item, now);
            });
        }
    }

    // Unowned: out of a player's hands or inventory onto the ground at their feet.
    private static void ToWorld(NetworkedItem item, LiveItems.LiveItem record)
    {
        if (record.Placement is not (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory))
            return;
        ServerPlayer holder = PlayerNumbers.Session(record.Holder);
        if (holder != null)
            item.transform.position = holder.WorldPosition + Vector3.up;
        if (Retrieval.DropFromHolder(item, record, holder,
                ItemChangeLog.Move(ItemMoveReason.HostCorrection, holder, RepairDetail), out string refused))
            Multiplayer.Log($"Invariant repair: {record.PrefabName} (netId {record.NetId}) went into the world");
        else
            Multiplayer.LogWarning($"Invariant repair: {record.PrefabName} (netId {record.NetId}) couldn't go into the world ({refused})");
    }

    // Vanilla's lists follow the home (rule 5): the item comes out of the lists it shouldn't be in.
    private static void MirrorLists(LiveItems.LiveItem record)
    {
        if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null || item.Item == null)
            return;
        Home home = ledger.TryGetHome(record.PersistentItemId, out Home owned) ? owned : HomeOf(record);
        StorageMembership membership = StorageIntegration.GetMembership(item.Item);
        VanillaLists now = Lists(membership);
        VanillaLists want = HomeInvariant.ListsFor(home, now, CurrentContext());
        StorageMembership target = (membership & StorageMembership.ItemContainer) |
                                   (want.HasFlag(VanillaLists.Inventory) ? StorageMembership.Inventory : StorageMembership.None) |
                                   (want.HasFlag(VanillaLists.World) ? StorageMembership.World : StorageMembership.None) |
                                   (want.HasFlag(VanillaLists.LostAndFound) ? StorageMembership.LostAndFound : StorageMembership.None);
        if (target == membership)
            return;
        StorageIntegration.MoveTo(item.Item, target);
        Multiplayer.Log($"Invariant repair: {record.PrefabName} (netId {record.NetId}) is in vanilla's {want}, not {now}");
    }

    private static VanillaLists Lists(StorageMembership membership) =>
        (membership.HasFlag(StorageMembership.Inventory) ? VanillaLists.Inventory : VanillaLists.None) |
        (membership.HasFlag(StorageMembership.World) ? VanillaLists.World : VanillaLists.None) |
        (membership.HasFlag(StorageMembership.LostAndFound) ? VanillaLists.LostAndFound : VanillaLists.None);

    // Every live record of the id: objects, and items kept as data with a record.
    private static List<LiveItems.LiveItem> Copies(Guid id) => LiveItems.GetAll()
        .Where(record => record.PersistentItemId == id &&
                         record.Placement is not (ItemPlacementKind.Destroyed or ItemPlacementKind.Container))
        .ToList();
}
