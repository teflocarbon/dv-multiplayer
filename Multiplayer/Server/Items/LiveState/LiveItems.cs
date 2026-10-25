using DV.InventorySystem;
using Multiplayer.Core.Items.Access;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.ChangeCauses;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.LiveState;

/// <summary>
/// Host-owned source of truth for item identity, placement, persistent ownership and
/// inventory claims. Unity object fields are projections of these records, never peers.
/// Owners and holders are identities; a session number is worked out only for the wire
/// (<see cref="PlayerNumbers"/>), so nothing is bound when a player joins or leaves.
/// </summary>
public static partial class LiveItems
{
    public sealed class LiveItem
    {
        public ushort NetId;
        public uint Revision;
        public ItemPlacementKind Placement;
        /// <summary>The identity of the player whose hand or inventory it's in, or empty. They may be offline.</summary>
        public Guid Holder;
        public PlayerHand PlacementHand;
        /// <summary>Its slot of the holder's inventory, for every inventory item, owned or not (OI-SLOT-01); else -1.</summary>
        public int Slot = -1;
        /// <summary>
        /// The owner's identity, read from the ledger (OI-LED-05), or empty for an unowned item. An
        /// offline owner is an identity with no session.
        /// </summary>
        public Guid Owner => ServerLedger.OwnerOf(PersistentItemId, StoredOwner);
        /// <summary>The owner the item's own store named, kept only until the ledger has it: the load's check reads it.</summary>
        public Guid StoredOwner;
        /// <summary>An essential's claim slot, read from the ledger: the slot of its owner's inventory it comes back to, or -1.</summary>
        public int InventoryClaimSlot => ServerLedger.ClaimOf(PersistentItemId, StoredClaimSlot);
        /// <summary>The claim the item's own store named, kept only until the ledger has it.</summary>
        public int StoredClaimSlot = -1;
        /// <summary>How its claim shows (dropped, stolen, its slot locked); none without a claim.</summary>
        public ItemInventoryClaimFlags InventoryClaimFlags
        {
            get => InventoryClaimSlot < 0 ? ItemInventoryClaimFlags.None : claimFlags | ItemInventoryClaimFlags.Reserved;
            set => claimFlags = value;
        }
        private ItemInventoryClaimFlags claimFlags;
        public string PrefabName = string.Empty;
        public Vector3 Position;
        public Quaternion Rotation;
        public ItemTransitionReason LastReason;
        // Why its placement, holder or owner last changed (ItemChangeLog).
        public ChangeCause<ItemMoveReason> LastMove;
        public Guid PersistentItemId;
        public string AuthoredItemKey = string.Empty;
        public ItemPlacementKind BaselinePlacement;
        public Vector3 BaselinePosition;
        public Quaternion BaselineRotation;
        public ItemWorldParentKind WorldParentKind;
        public ushort WorldParentNetId;
        public string WorldParentKey = string.Empty;
        public string WorldParentPersistentId = string.Empty;
        public Vector3 ParentLocalPosition;
        public Quaternion ParentLocalRotation;
        public bool HasPersistentOverride;
        public bool IsReplenishableStockFork;
        public ushort AttachedCarNetId;
        public string AttachedCarPersistentId = string.Empty;
        public bool AttachedFront;
        // The tool whose magazine it's in (Placement Magazine): its NetId, its id and the slot.
        public ushort MagazineNetId;
        public Guid MagazineToolId;
        public int MagazineSlot = -1;
        // The revisions that changed only object state; see ItemPlacementRevision.
        public uint ObjectStateRunBase;
        public uint ObjectStateRunTop;
        // The player whose own settle commits were the last placement changes; see ItemPlacementRevision.UnchangedFor.
        public Guid SettledBy;
        public uint SettledBase;
        public uint SettledTop;

        private static readonly System.Reflection.FieldInfo[] Fields = typeof(LiveItem).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        internal LiveItem Copy() => (LiveItem)MemberwiseClone();

        // Puts back what a refused commit changed.
        internal void RestoreFrom(LiveItem copy)
        {
            foreach (System.Reflection.FieldInfo field in Fields)
                field.SetValue(this, field.GetValue(copy));
        }
    }

    private static readonly Dictionary<ushort, LiveItem> records = new();

    public static bool TryGet(ushort netId, out LiveItem record) => records.TryGetValue(netId, out record);
    public static IEnumerable<LiveItem> GetAll() => records.Values;

    public static void MarkAuthoredIdentity(NetworkedItem item)
    {
        if (item == null || !item.IsSceneAuthored || !records.TryGetValue(item.NetId, out LiveItem record))
            return;
        record.AuthoredItemKey = item.AuthoredItemKey;
        record.PersistentItemId = Guid.Empty;
        ItemIdentity identity = item.GetComponent<ItemIdentity>();
        if (identity != null)
            UnityEngine.Object.Destroy(identity);
    }

    public static Guid PromoteAuthoredToPersistent(NetworkedItem item,
        bool replenishableStockFork = false)
    {
        if (item == null || !records.TryGetValue(item.NetId, out LiveItem record))
            return Guid.Empty;
        ItemIdentity identity = item.GetComponent<ItemIdentity>() ??
                                               item.gameObject.AddComponent<ItemIdentity>();
        Guid persistentId = identity.Ensure();
        record.PersistentItemId = persistentId;
        record.AuthoredItemKey = string.Empty;
        record.HasPersistentOverride = true;
        record.IsReplenishableStockFork |= replenishableStockFork;
        return persistentId;
    }

    public static LiveItem Ensure(NetworkedItem item, ServerPlayer actor = null, ItemUpdate seed = null)
    {
        if (item == null || item.NetId == 0)
            return null;
        if (records.TryGetValue(item.NetId, out LiveItem existing))
            return existing;

        // No owner here: an item becomes owned only by a ledger commit (a first take, a purchase, a
        // grant), never because a record was made while someone held it.
        ItemIdentity persistentIdentity = item.GetComponent<ItemIdentity>();
        Guid persistentItemId = item.IsSceneAuthored ? Guid.Empty : ServerLedger.ClaimPersistentId(item,
            (persistentIdentity ?? item.gameObject.AddComponent<ItemIdentity>()).Ensure());
        ItemPlacementKind initialPlacement = PlacementFrom(seed?.ItemState ?? item.DebugCurrentState);
        Vector3 initialPosition = seed?.ItemPosition ?? item.transform.position - WorldMover.currentMove;
        Quaternion initialRotation = seed?.ItemRotation ?? item.transform.rotation;
        LiveItem record = new()
        {
            NetId = item.NetId,
            Placement = initialPlacement,
            // Without a seed (an item the host just created or restored) this used to read
            // seed.PlayerId and throw, so the caller's registration never finished.
            Holder = seed == null || seed.PlayerId == 0 ? Guid.Empty
                : actor != null && actor.PlayerId == seed.PlayerId ? actor.Guid : PlayerNumbers.Identity(seed.PlayerId),
            PlacementHand = initialPlacement == ItemPlacementKind.PlayerHand
                ? PlayerHandRules.Normalize(seed?.PlayerHand ?? PlayerHand.None)
                : PlayerHand.None,
            Slot = initialPlacement == ItemPlacementKind.PlayerInventory ? seed?.InventorySlot ?? -1 : -1,
            PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
            Position = initialPosition,
            Rotation = initialRotation,
            LastReason = ItemTransitionReason.InitialRegistration,
            PersistentItemId = persistentItemId,
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty,
            BaselinePlacement = initialPlacement,
            BaselinePosition = initialPosition,
            BaselineRotation = initialRotation,
            WorldParentKind = seed?.WorldParentKind ?? ItemWorldParentKind.World,
            WorldParentNetId = seed?.WorldParentNetId ?? 0,
            WorldParentKey = seed?.WorldParentKey ?? string.Empty,
            ParentLocalPosition = seed?.ParentLocalPosition ?? Vector3.zero,
            ParentLocalRotation = seed?.ParentLocalRotation ?? Quaternion.identity,
            AttachedCarNetId = seed?.CarNetId ?? 0,
            AttachedFront = seed?.AttachedFront ?? true,
            MagazineNetId = initialPlacement == ItemPlacementKind.Magazine ? seed?.MagazineNetId ?? 0 : (ushort)0,
            MagazineToolId = initialPlacement == ItemPlacementKind.Magazine ? ToolIdOf(seed?.MagazineNetId ?? 0) : Guid.Empty,
            MagazineSlot = initialPlacement == ItemPlacementKind.Magazine ? seed?.MagazineSlot ?? -1 : -1
        };
        records[item.NetId] = record;
        Publish("item.canonical-record-created", item, record, null);
        return record;
    }

    /// <param name="cause">
    /// Why, when the caller knows (a disconnect, a restore). Without it a change of placement,
    /// holder or owner is named from what changed (<see cref="ItemMoveClassifier"/>).
    /// </param>
    public static bool TryApplyTransition(NetworkedItem item, ItemUpdate snapshot, ServerPlayer actor,
        ItemTransitionReason reason, bool force, out string rejectionReason,
        bool clearRetrievalClaim = false, ChangeCause<ItemMoveReason>? cause = null)
    {
        if (actor == null)
        {
            rejectionReason = "invalid-transition-input";
            return false;
        }
        return ApplyTransition(item, snapshot, actor, reason, force, out rejectionReason, clearRetrievalClaim, cause);
    }

    /// <summary>
    /// A change the server's own world made to an item, committed by the server itself rather than
    /// a player (an empty <see cref="ItemTransition.Actor"/>): on a host with no player of
    /// its own (<see cref="HostItemActor.IsServer"/>). It can move the item about the world but
    /// not into anyone's hands.
    /// </summary>
    public static bool TryApplyServerTransition(NetworkedItem item, ItemUpdate snapshot,
        ItemTransitionReason reason, bool force, out string rejectionReason,
        ChangeCause<ItemMoveReason>? cause = null) =>
        ApplyTransition(item, snapshot, null, reason, force, out rejectionReason, false, cause);

    /// <summary>
    /// What vanilla did to an installed gadget's item, committed by the server's gadget code: the
    /// only transition into or out of <see cref="ItemPlacementKind.Installed"/> (Server/Gadgets).
    /// </summary>
    public static bool TryApplyGadgetTransition(NetworkedItem item, ItemUpdate snapshot, ServerPlayer actor,
        ItemTransitionReason reason, out string rejectionReason, ChangeCause<ItemMoveReason>? cause = null) =>
        ApplyTransition(item, snapshot, actor, reason, true, out rejectionReason, false, cause, gadgetTransition: true);

    // actor null: the server itself.
    private static bool ApplyTransition(NetworkedItem item, ItemUpdate snapshot, ServerPlayer actor,
        ItemTransitionReason reason, bool force, out string rejectionReason,
        bool clearRetrievalClaim, ChangeCause<ItemMoveReason>? cause, bool gadgetTransition = false)
    {
        rejectionReason = string.Empty;
        if (item == null || snapshot == null || item.NetId == 0)
        {
            rejectionReason = "invalid-transition-input";
            return false;
        }
        // The server itself is no one: a console sender has no identity either.
        Guid actorIdentity = actor?.Guid ?? Guid.Empty;
        byte actorId = actor?.PlayerId ?? 0;

        // A cause a client put in its own update is never passed on: only the host's goes out.
        snapshot.MoveCause = null;
        LiveItem record = Ensure(item, actor, snapshot);
        ItemWhereabouts before = ItemChangeLog.Whereabouts(record);
        bool retrievalEligible = item.Item?.InventorySpecs?.IsEssential == true;
        ItemPlacementKind previousPlacement = record.Placement;
        PlayerHand previousPlacementHand = record.PlacementHand;
        LiveItemState currentState = ToCore(record);
        if (!retrievalEligible)
        {
            // Sanitize the candidate state, not the live record. A rejected transition
            // must never mutate canonical authority; the cleanup commits only when the
            // transition itself is accepted.
            currentState.InventoryClaimSlot = -1;
            currentState.InventoryClaimFlags = ClaimFlags.None;
        }
        Publish("item.transition-requested", item, record, new()
        {
            ["actorPlayerId"] = actorId,
            ["requestedState"] = snapshot.ItemState.ToString(),
            ["requestedRevision"] = snapshot.AuthorityRevision,
            ["force"] = force,
            ["reason"] = reason.ToString()
        });

        bool appliesPlacement = ItemUpdate.ChangesPlacement(snapshot.UpdateType);
        ItemPlacementKind requestedPlacement = appliesPlacement ? PlacementFrom(snapshot.ItemState) : record.Placement;
        if (appliesPlacement && snapshot.ItemState is ItemState.Dropped or ItemState.Thrown)
            requestedPlacement = snapshot.WorldParentKind switch
            {
                ItemWorldParentKind.TrainInterior => ItemPlacementKind.TrainInterior,
                ItemWorldParentKind.StaticParent => ItemPlacementKind.StaticParent,
                _ => requestedPlacement
            };
        // Into a player's hands from the world, or from another player (OI-ACC-07). Asked before the
        // state machine, so the refusal of a take from another player is ItemAccess's; a placement
        // with a player is always the actor's (ItemStateMachine).
        if (actor != null && appliesPlacement &&
            requestedPlacement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
            TakeAction(record, actor.Guid) is { } take &&
            !ItemAccessServer.Allows(actor, take, item, record, out rejectionReason))
        {
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }
        // Out of a tool another player holds to anywhere but a hand (an eject, a drop): theirs to do (H1).
        if (actor != null && appliesPlacement && record.Placement == ItemPlacementKind.Magazine &&
            requestedPlacement is not (ItemPlacementKind.Magazine or ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory) &&
            ToolHeldByOther(record.MagazineNetId, actor.Guid))
        {
            rejectionReason = "magazine-tool-foreign";
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }
        if (appliesPlacement && requestedPlacement == ItemPlacementKind.Magazine &&
            MagazineRefusal(item, snapshot, actor) is { Length: > 0 } magazineRefusal)
        {
            rejectionReason = magazineRefusal;
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }
        TransitionResult result = ItemStateMachine.Apply(currentState, new ItemTransition
        {
            Actor = actorIdentity,
            ExpectedRevision = snapshot.AuthorityRevision,
            Force = force,
            AppliesPlacement = appliesPlacement,
            RequestedPlacement = (LivePlacement)(byte)requestedPlacement,
            ClearRetrievalClaim = clearRetrievalClaim,
            GadgetTransition = gadgetTransition,
            // Never a job paper: it is its job's (D5, OI-ID-12), though a booklet is BelongsToPlayer.
            MayEstablishPersistentOwner = item.Item?.InventorySpecs?.BelongsToPlayer == true && !Shared.Jobs.JobPapers.IsPaper(record.PrefabName),
            InventoryClaimSlot = retrievalEligible ? snapshot.InventoryClaimSlot : -1,
            InventoryClaimFlags = retrievalEligible
                ? (ClaimFlags)(byte)snapshot.InventoryClaimFlags
                : ClaimFlags.None
        });
        if (!result.Accepted)
        {
            rejectionReason = result.RejectionReason;
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }
        // Into a player's inventory, whatever route it took and whoever commits it: on a dedicated
        // host the server's own copy of a client's item reports the client's stash with no actor
        // (fixture-no-stash, 2026-10-21).
        if (result.State.Placement == LivePlacement.PlayerInventory && record.Placement != ItemPlacementKind.PlayerInventory &&
            (actor ?? PlayerNumbers.Session(result.State.Holder)) is ServerPlayer stasher &&
            !ItemAccessServer.Allows(stasher, ItemAccessAction.Stash, item, record, out rejectionReason))
        {
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }

        LiveItem previous = record.Copy();
        ApplyCore(record, result.State);
        record.PlacementHand = appliesPlacement
            ? record.Placement == ItemPlacementKind.PlayerHand
                ? PlayerHandRules.Normalize(snapshot.PlayerHand)
                : PlayerHand.None
            : previousPlacementHand;
        KeepRemotePossession(item, record, snapshot);
        // Every inventory item has its slot. A player's request names it, and gets that one or a
        // refusal (OI-SLOT-03); a server-made move keeps the one it had, else its claim or the
        // lowest free one.
        record.Slot = record.Placement != ItemPlacementKind.PlayerInventory ? -1
            : !appliesPlacement ? previous.Slot
            : actor != null && snapshot.InventorySlot >= 0 ? snapshot.InventorySlot
            : ServerLedger.FreeSlot(record, record.Holder, snapshot.InventorySlot,
                previous.Placement == ItemPlacementKind.PlayerInventory && previous.Holder == record.Holder ? previous.Slot : -1);
        record.Position = snapshot.ItemPosition;
        record.Rotation = snapshot.ItemRotation;
        record.WorldParentKind = snapshot.WorldParentKind;
        record.WorldParentNetId = snapshot.WorldParentNetId;
        record.WorldParentKey = snapshot.WorldParentKey ?? string.Empty;
        if (record.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            NetworkedTrainCar.TryGet(record.WorldParentNetId, out TrainCar parentCar))
            record.WorldParentPersistentId = parentCar.CarGUID ?? string.Empty;
        else if (record.WorldParentKind != ItemWorldParentKind.TrainInterior)
            record.WorldParentPersistentId = string.Empty;
        record.ParentLocalPosition = snapshot.ParentLocalPosition;
        record.ParentLocalRotation = snapshot.ParentLocalRotation;
        record.AttachedCarNetId = snapshot.CarNetId;
        record.AttachedFront = snapshot.AttachedFront;
        if (appliesPlacement)
        {
            bool inMagazine = record.Placement == ItemPlacementKind.Magazine;
            // A scene tool a player loads takes an id of its own, which its ammo's home names, and
            // becomes an ordinary item as every promoted scene item does (its scene slot tombstoned,
            // its key let go), so a save keeps it once. One the scene made loaded stays the scene's,
            // its ammo with it.
            if (inMagazine && actor != null && NetworkedItem.TryGet(snapshot.MagazineNetId, out NetworkedItem tool) &&
                records.TryGetValue(snapshot.MagazineNetId, out LiveItem toolRecord) && toolRecord.PersistentItemId == Guid.Empty)
            {
                if (tool.IsSceneAuthored)
                    ItemSaveLoad.RecordDestroyed(tool);
                PromoteAuthoredToPersistent(tool);
                tool.DetachAuthoredItemKey();
            }
            record.MagazineNetId = inMagazine ? snapshot.MagazineNetId : (ushort)0;
            record.MagazineToolId = inMagazine ? ToolIdOf(snapshot.MagazineNetId) : Guid.Empty;
            record.MagazineSlot = inMagazine ? snapshot.MagazineSlot : -1;
        }
        // The car an end-of-train lantern hangs on, or an installed gadget sits on.
        if ((record.Placement is ItemPlacementKind.Attached or ItemPlacementKind.Installed) &&
            NetworkedTrainCar.TryGet(record.AttachedCarNetId, out TrainCar attachedCar))
            record.AttachedCarPersistentId = attachedCar.CarGUID ?? string.Empty;
        if (item.IsSceneAuthored && snapshot.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.ObjectState) &&
            snapshot.States is { Count: > 0 })
            record.HasPersistentOverride = true;
        record.LastReason = reason;
        ItemWhereabouts after = ItemChangeLog.Whereabouts(record);
        if (cause == null && !ItemMoveClassifier.Changed(before, after) && record.Slot != previous.Slot &&
            previous.Placement == ItemPlacementKind.PlayerInventory)
            cause = ItemChangeLog.Move(ItemMoveReason.SlotMoved, actor, $"slot {previous.Slot} -> {record.Slot}");
        // A caller that names a cause moved it on purpose, if only to another spot in the world.
        bool moved = cause.HasValue || ItemMoveClassifier.Changed(before, after);
        ChangeCause<ItemMoveReason> move = moved
            ? TransitionCause(item, reason, actor, before, after, snapshot.ItemState == ItemState.Thrown, cause)
            : default;
        // The ledger first (OI-LED-04): a commit it refuses leaves the record, the ledger and every
        // game as they were, and the sender is corrected.
        string refusal = record.Slot < 0 && record.Placement == ItemPlacementKind.PlayerInventory
            ? "inventory-full"
            : ServerLedger.CommitTransition(record, ServerLedger.HomeOf(previous), ServerLedger.HomeOf(record),
                previous.Owner == Guid.Empty ? result.State.Owner : Guid.Empty, move);
        if (refusal.Length > 0)
        {
            record.RestoreFrom(previous);
            rejectionReason = refusal;
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }
        WriteToSnapshot(record, snapshot, reason);
        if (moved)
        {
            snapshot.MoveCause = move;
            ItemChangeLog.RecordMove(item, record, move);
        }
        Publish("item.transition-accepted", item, record, new()
        {
            ["actorPlayerId"] = actorId,
            ["reason"] = reason.ToString()
        });
        ServerItemManager.Instance?.OnAuthoritativeItemTransition(item, snapshot, record, actor,
            previousPlacement, appliesPlacement);
        // Into (or within) an inventory: its object goes to data in the next frame (D14).
        PlayerItems.Stored(record);
        return true;
    }

    // Into a tool's magazine (containers.md, "Ammo in a magazine"): a live tool whose magazine takes
    // this ammo, in a slot nothing else has, and not one in another player's hands or inventory.
    private static string MagazineRefusal(NetworkedItem item, ItemUpdate snapshot, ServerPlayer actor)
    {
        if (!NetworkedItem.TryGet(snapshot.MagazineNetId, out NetworkedItem tool) || tool == null || tool == item ||
            !records.TryGetValue(tool.NetId, out LiveItem toolRecord) ||
            toolRecord.Placement is ItemPlacementKind.Destroyed or ItemPlacementKind.LostAndFound or ItemPlacementKind.Magazine)
            return "magazine-tool-unknown";
        if (actor != null && ToolHeldByOther(tool.NetId, actor.Guid))
            return "magazine-tool-foreign";
        DV.Items.ItemMagazine magazine = tool.GetComponentInChildren<DV.Items.ItemMagazine>(true);
        if (magazine == null || snapshot.MagazineSlot < 0 || snapshot.MagazineSlot >= magazine.Capacity)
            return "magazine-slot-invalid";
        if (!magazine.ValidItem(item.gameObject))
            return "magazine-refused-item";
        if (records.Values.Any(other => other.NetId != item.NetId && other.Placement == ItemPlacementKind.Magazine &&
                                        other.MagazineNetId == tool.NetId && other.MagazineSlot == snapshot.MagazineSlot))
            return "magazine-slot-taken";
        return string.Empty;
    }

    // A tool in another player's hands or inventory: what's in its magazine is theirs to load and take out.
    private static bool ToolHeldByOther(ushort toolNetId, Guid actor) =>
        records.TryGetValue(toolNetId, out LiveItem tool) && tool.Holder != Guid.Empty && tool.Holder != actor &&
        tool.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory;

    private static Guid ToolIdOf(ushort toolNetId) =>
        toolNetId != 0 && records.TryGetValue(toolNetId, out LiveItem tool) ? tool.PersistentItemId : Guid.Empty;

    /// <summary>The ammo loaded in a tool's magazine (containers.md, "Ammo in a magazine").</summary>
    public static IEnumerable<LiveItem> AmmoIn(ushort toolNetId) => toolNetId == 0
        ? Enumerable.Empty<LiveItem>()
        : records.Values.Where(record => record.Placement == ItemPlacementKind.Magazine && record.MagazineNetId == toolNetId).ToArray();

    // What taking it into this player's hands is: from the world, from another player, or neither.
    // Ammo is taken from wherever its tool is: out of a tool another player holds or has stashed,
    // it's a take from that player (H1).
    private static ItemAccessAction? TakeAction(LiveItem record, Guid taker) => record.Placement switch
    {
        ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory =>
            record.Holder != Guid.Empty && record.Holder != taker ? ItemAccessAction.TakeFromPlayer : null,
        ItemPlacementKind.Magazine when ToolHeldByOther(record.MagazineNetId, taker) => ItemAccessAction.TakeFromPlayer,
        ItemPlacementKind.World or ItemPlacementKind.TrainInterior or ItemPlacementKind.StaticParent or
            ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment or ItemPlacementKind.Magazine => ItemAccessAction.TakeFromWorld,
        _ => null
    };

    private static ChangeCause<ItemMoveReason> TransitionCause(NetworkedItem item, ItemTransitionReason reason,
        ServerPlayer actor, ItemWhereabouts before, ItemWhereabouts after, bool thrown,
        ChangeCause<ItemMoveReason>? given)
    {
        string change = ItemChangeLog.Describe(before, after);
        if (given is { } known)
            return known.WithDetail(ItemChangeCauses.Detail(change, known.Detail));
        ItemMoveReason classified = reason switch
        {
            ItemTransitionReason.PersistenceRestore => ItemMoveReason.RestoredFromSave,
            ItemTransitionReason.JobBookletSummon => ItemMoveReason.JobBookletSummoned,
            _ => ItemMoveClassifier.Classify(before, after, thrown)
        };
        string source = reason switch
        {
            ItemTransitionReason.ClientState => null,
            // The host's own copy changed: on a dedicated host that is the game, not a player.
            ItemTransitionReason.HostLocalState => string.IsNullOrEmpty(item.ServerPart?.ObservationTrigger)
                ? "host's game" : "host's game, " + item.ServerPart?.ObservationTrigger,
            _ => reason.ToString()
        };
        return ItemChangeLog.Move(classified, actor, ItemChangeCauses.Detail(change, source,
            classified == ItemMoveReason.Unknown ? "callers " + ItemChangeCauses.Callers(typeof(LiveItems)) : null));
    }

    /// <summary>
    /// Puts the record's pose into an object-state update before it's applied, so a transition
    /// that doesn't move the item doesn't take the sender's copy's position as the item's.
    /// </summary>
    public static void KeepRecordedPose(NetworkedItem item, ItemUpdate snapshot)
    {
        if (item == null || snapshot == null || !records.TryGetValue(item.NetId, out LiveItem record))
            return;
        snapshot.ItemPosition = record.Position;
        snapshot.ItemRotation = record.Rotation;
        snapshot.WorldParentKind = record.WorldParentKind;
        snapshot.WorldParentNetId = record.WorldParentNetId;
        snapshot.WorldParentKey = record.WorldParentKey ?? string.Empty;
        snapshot.ParentLocalPosition = record.ParentLocalPosition;
        snapshot.ParentLocalRotation = record.ParentLocalRotation;
        snapshot.CarNetId = record.AttachedCarNetId;
        snapshot.AttachedFront = record.AttachedFront;
        snapshot.MagazineNetId = record.Placement == ItemPlacementKind.Magazine ? record.MagazineNetId : (ushort)0;
        snapshot.MagazineSlot = record.Placement == ItemPlacementKind.Magazine ? record.MagazineSlot : -1;
    }

    /// <summary>
    /// Whether the item is where it was at <paramref name="revision"/>: only object state (a
    /// lantern's flame) has changed since. A physics lease granted then is still good.
    /// </summary>
    public static bool PlacementUnchangedSince(LiveItem record, uint revision) =>
        record != null && ItemPlacementRevision.UnchangedSince(revision, record.Revision,
            record.ObjectStateRunBase, record.ObjectStateRunTop);

    public static bool TryCommitSpatialPose(NetworkedItem item, uint expectedRevision,
        ItemSpatialStateData state, out LiveItem record, out string rejectionReason, Guid settler = default)
    {
        record = null;
        rejectionReason = string.Empty;
        if (item == null || state == null || item.NetId == 0 || state.ItemNetId != item.NetId ||
            !records.TryGetValue(item.NetId, out record))
        {
            rejectionReason = "invalid-spatial-commit-input";
            return false;
        }
        if (!PlacementUnchangedSince(record, expectedRevision) || state.AuthorityRevision != expectedRevision)
        {
            rejectionReason = "stale-spatial-authority-revision";
            return false;
        }
        if (record.Placement is not (ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
            ItemPlacementKind.StaticParent))
        {
            rejectionReason = "spatial-commit-placement-changed";
            return false;
        }
        if (record.Revision == uint.MaxValue)
        {
            rejectionReason = "spatial-authority-revision-exhausted";
            return false;
        }

        ItemWhereabouts before = ItemChangeLog.Whereabouts(record);
        // The simulating player knows where it came to rest: their own requests made before this
        // commit still hold (ItemPlacementRevision.UnchangedFor). The server's own settling has none.
        (record.SettledBy, record.SettledBase, record.SettledTop) = ItemPlacementRevision.SettledAfter(settler,
            record.Revision, record.ObjectStateRunBase, record.ObjectStateRunTop,
            record.SettledBy, record.SettledBase, record.SettledTop);
        record.Revision++;
        record.ObjectStateRunBase = record.ObjectStateRunTop = 0;
        record.Placement = state.WorldParentKind switch
        {
            ItemWorldParentKind.TrainInterior => ItemPlacementKind.TrainInterior,
            ItemWorldParentKind.StaticParent => ItemPlacementKind.StaticParent,
            _ => ItemPlacementKind.World
        };
        record.Holder = Guid.Empty;
        record.PlacementHand = PlayerHand.None;
        record.Position = state.AbsolutePosition;
        record.Rotation = state.Rotation;
        record.WorldParentKind = state.WorldParentKind;
        record.WorldParentNetId = state.WorldParentNetId;
        record.WorldParentKey = state.WorldParentKey ?? string.Empty;
        record.ParentLocalPosition = state.ParentLocalPosition;
        record.ParentLocalRotation = state.ParentLocalRotation;
        if (record.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            NetworkedTrainCar.TryGet(record.WorldParentNetId, out TrainCar parentCar))
            record.WorldParentPersistentId = parentCar.CarGUID ?? string.Empty;
        else if (record.WorldParentKind != ItemWorldParentKind.TrainInterior)
            record.WorldParentPersistentId = string.Empty;
        record.HasPersistentOverride = true;
        record.LastReason = ItemTransitionReason.SpatialSettlement;
        // Coming to rest where it was thrown isn't news, and would hide who threw it: only a
        // settlement that lands it somewhere else (into a car) is recorded.
        // The ledger takes where it landed either way: from one car into another is a new home.
        ItemWhereabouts after = ItemChangeLog.Whereabouts(record);
        ChangeCause<ItemMoveReason> settled = ItemChangeLog.Move(ItemMoveReason.Settled,
            detail: ItemChangeLog.Describe(before, after));
        ServerLedger.OnSettled(record, settled);
        if (ItemMoveClassifier.Changed(before, after))
            ItemChangeLog.RecordMove(item, record, settled);
        item.ApplyAuthorityMetadata(AuthorityMetadata(record, record.Revision, record.LastReason));
        Publish("item.spatial-pose-committed", item, record, new()
        {
            ["simulationEpoch"] = state.SimulationEpoch,
            ["sampleSequence"] = state.SampleSequence,
            ["worldParentKind"] = state.WorldParentKind.ToString()
        });
        return true;
    }

    public static void ApplyReplicaMetadata(NetworkedItem item, ItemUpdate snapshot)
    {
        if (item == null || snapshot == null || item.NetId == 0)
            return;
        item.ApplyAuthorityMetadata(snapshot);
    }

    /// <summary>
    /// A record's authority fields for <see cref="NetworkedItem.ApplyAuthorityMetadata"/> on the
    /// server's own copy: the host's player's relation to it (<see cref="PlayerNumbers.LocalRelation"/>).
    /// </summary>
    public static ItemUpdate AuthorityMetadata(LiveItem record, uint revision, ItemTransitionReason reason) => new()
    {
        ItemNetId = record.NetId,
        AuthorityRevision = revision,
        Relation = PlayerNumbers.LocalRelation(record.Owner),
        InventoryClaimSlot = record.InventoryClaimSlot,
        InventoryClaimFlags = record.InventoryClaimFlags,
        TransitionReason = reason
    };

    /// <summary>
    /// Who is told an item's claim: its owner; for an unowned item (a withdrawn one), its holder,
    /// whose inventory slot it is. Everyone else gets none (<see cref="ItemUpdate.For"/>).
    /// </summary>
    public static Guid Claimant(LiveItem record) =>
        record == null ? Guid.Empty : record.Owner != Guid.Empty ? record.Owner : record.Holder;

    public static void WriteToSnapshot(LiveItem record, ItemUpdate snapshot, ItemTransitionReason reason)
    {
        snapshot.AuthorityRevision = record.Revision;
        snapshot.Relation = PlayerNumbers.LocalRelation(record.Owner);
        snapshot.InventoryClaimSlot = record.InventoryClaimSlot;
        snapshot.InventoryClaimFlags = record.InventoryClaimFlags;
        snapshot.TransitionReason = reason;
        snapshot.PlayerId = PlayerNumbers.Of(record.Holder);
        snapshot.PlayerHand = record.Placement == ItemPlacementKind.PlayerHand
            ? record.PlacementHand : PlayerHand.None;
        snapshot.InventorySlot = record.Placement == ItemPlacementKind.PlayerInventory ? record.Slot : -1;
        snapshot.WorldParentKind = record.WorldParentKind;
        snapshot.WorldParentNetId = record.WorldParentNetId;
        snapshot.WorldParentKey = record.WorldParentKey ?? string.Empty;
        snapshot.ParentLocalPosition = record.ParentLocalPosition;
        snapshot.ParentLocalRotation = record.ParentLocalRotation;
        snapshot.CarNetId = record.AttachedCarNetId;
        snapshot.AttachedFront = record.AttachedFront;
        snapshot.MagazineNetId = record.Placement == ItemPlacementKind.Magazine ? record.MagazineNetId : (ushort)0;
        snapshot.MagazineSlot = record.Placement == ItemPlacementKind.Magazine ? record.MagazineSlot : -1;
        snapshot.ItemPosition = record.Position;
        snapshot.ItemRotation = record.Rotation;
    }

    private static void WriteStateToSnapshot(LiveItemState state, ItemUpdate snapshot,
        ItemTransitionReason reason)
    {
        snapshot.AuthorityRevision = state.Revision;
        snapshot.Relation = PlayerNumbers.LocalRelation(state.Owner);
        snapshot.InventoryClaimSlot = state.InventoryClaimSlot;
        snapshot.InventoryClaimFlags = (ItemInventoryClaimFlags)(byte)state.InventoryClaimFlags;
        snapshot.TransitionReason = reason;
        snapshot.PlayerId = PlayerNumbers.Of(state.Holder);
        snapshot.PlayerHand = PlayerHand.None;
    }

    /// <summary>
    /// Builds a host-authored correction for a rejected speculative client transition.
    /// The registry supplies identity and placement; the Unity item supplies any state-specific
    /// attachment/transform fields needed by the normal snapshot application path.
    /// </summary>
    /// <param name="cause">Why the recipient is being corrected, logged on their side (a refusal's reason).</param>
    public static bool TryCreateCorrectionSnapshot(NetworkedItem item, out ItemUpdate snapshot,
        ChangeCause<ItemMoveReason>? cause = null)
    {
        snapshot = null;
        if (item == null || item.NetId == 0 || !records.TryGetValue(item.NetId, out LiveItem record))
            return false;

        snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.ItemState);
        if (snapshot == null)
            return false;

        snapshot.ItemState = record.Placement switch
        {
            ItemPlacementKind.PlayerHand => ItemState.InHand,
            ItemPlacementKind.PlayerInventory => ItemState.InInventory,
            ItemPlacementKind.Attached => ItemState.Attached,
            ItemPlacementKind.Installed => ItemState.Installed,
            ItemPlacementKind.Magazine => ItemState.InMagazine,
            _ => ItemState.Dropped
        };
        snapshot.ItemPosition = record.Position;
        snapshot.ItemRotation = record.Rotation;
        snapshot.OriginatingPlayerId = 0;
        WriteToSnapshot(record, snapshot, record.LastReason);
        snapshot.MoveCause = cause;
        return true;
    }

    public static Dictionary<string, object> Snapshot(LiveItem record)
    {
        if (record == null)
            return new();
        return new Dictionary<string, object>
        {
            ["netId"] = record.NetId,
            ["revision"] = record.Revision,
            ["placement"] = record.Placement.ToString(),
            // The numbers are what the wire says now; the identities are the record.
            ["placementPlayerId"] = PlayerNumbers.Of(record.Holder),
            ["placementHand"] = record.PlacementHand.ToString(),
            ["slot"] = record.Slot,
            ["placementPlayerIdentity"] = record.Holder == Guid.Empty ? string.Empty : record.Holder.ToString("D"),
            ["persistentOwnerPlayerId"] = PlayerNumbers.Of(record.Owner),
            ["persistentOwnerIdentity"] = record.Owner == Guid.Empty ? string.Empty : record.Owner.ToString("D"),
            ["inventoryClaimPlayerId"] = record.InventoryClaimSlot < 0 ? (byte)0 : PlayerNumbers.Of(Claimant(record)),
            ["inventoryClaimSlot"] = record.InventoryClaimSlot,
            ["inventoryClaimFlags"] = record.InventoryClaimFlags.ToString(),
            ["prefabName"] = record.PrefabName,
            ["lastReason"] = record.LastReason.ToString(),
            ["lastMove"] = record.LastMove.Reason == ItemMoveReason.Unknown && record.LastMove.Detail.Length == 0
                ? string.Empty : record.LastMove.ToString(),
            ["persistentItemId"] = record.PersistentItemId == Guid.Empty ? string.Empty : record.PersistentItemId.ToString("D"),
            ["authoredItemKey"] = record.AuthoredItemKey ?? string.Empty,
            ["worldParentKind"] = record.WorldParentKind.ToString(),
            ["worldParentNetId"] = record.WorldParentNetId,
            ["worldParentKey"] = record.WorldParentKey ?? string.Empty
            ,["worldParentPersistentId"] = record.WorldParentPersistentId ?? string.Empty
            ,["hasPersistentOverride"] = record.HasPersistentOverride
            ,["attachedCarNetId"] = record.AttachedCarNetId
            ,["attachedCarPersistentId"] = record.AttachedCarPersistentId ?? string.Empty
            ,["attachedFront"] = record.AttachedFront
        };
    }

    /// <summary>Whether any record is owned by, or held by, this player ID.</summary>
    internal static bool HasIdentity(Guid identity) =>
        identity != Guid.Empty && records.Values.Any(record =>
            record.Owner == identity || record.Holder == identity);

    /// <summary>A player's ID changed (InstallGuidRekey): their records follow. Returns how many changed.</summary>
    internal static int RekeyIdentity(Guid from, Guid to)
    {
        if (from == Guid.Empty || to == Guid.Empty)
            return 0;
        int changed = 0;
        foreach (LiveItem record in records.Values)
        {
            bool touched = false;
            if (record.StoredOwner == from)
            {
                record.StoredOwner = to;
                touched = true;
            }
            if (record.Holder == from)
            {
                record.Holder = to;
                touched = true;
            }
            if (touched)
                changed++;
        }
        return changed;
    }

    /// <summary>
    /// A claim the ledger moved (one essential took another's slot, OI-ESS-01): the owner's game is
    /// told, and puts the silhouette in the new slot. The revision stays: only the owner's metadata
    /// changes, and a holder's next transition must not be refused as stale for it.
    /// </summary>
    internal static void SendClaim(ushort netId, int fromSlot)
    {
        if (!records.TryGetValue(netId, out LiveItem record))
            return;
        NetworkedItem.TryGet(netId, out NetworkedItem item);
        Publish("item.essential-claim-slot-moved", item, record, new()
        {
            ["fromSlot"] = fromSlot,
            ["toSlot"] = record.InventoryClaimSlot
        });
        record.LastMove = ItemChangeLog.Move(ItemMoveReason.ClaimSlotMoved,
            detail: $"claim slot {fromSlot} -> {record.InventoryClaimSlot}");
        PushClaimSlotCorrection(item, record);
    }

    private static void PushClaimSlotCorrection(NetworkedItem item, LiveItem record)
    {
        bool lost = record.Placement == ItemPlacementKind.LostAndFound;
        if (lost)
            LostAndFoundServer.SetInventoryClaimSlot(record.NetId, record.InventoryClaimSlot);
        if (item == null)
            return;
        ItemUpdate correction = null;
        bool corrected = lost
            ? LostAndFoundServer.TryCreateCollectionProjection(item, out correction)
            : TryCreateCorrectionSnapshot(item, out correction, record.LastMove);
        NetworkServer server = NetworkServer.Instance;
        bool hostOwns = server?.LocalPlayer != null && record.Owner != Guid.Empty &&
            record.Owner == server.LocalPlayer.Guid;
        if (corrected && hostOwns && !lost)
            item.ApplyServerCanonicalSnapshot(correction);
        else
            item.ApplyAuthorityMetadata(AuthorityMetadata(record, item.AuthorityRevision, item.LastTransitionReason));
        // An owner still loading gets the claim with the rest of their items.
        if (corrected && server != null && !hostOwns &&
            PlayerNumbers.TryGetSession(record.Owner, out ServerPlayer owner) &&
            owner.LoadingState >= PlayerLoadingState.ReadyForItems)
            server.SendItemUpdate(correction, owner);
    }


    public static void Remove(ushort netId)
    {
        if (records.TryGetValue(netId, out LiveItem record))
            EndLease(record, "record-removed");
        records.Remove(netId);
    }

    // Every change that ends an item's physics lease ends it here, explicitly (OI-PHY-02), when it
    // doesn't pass through ApplyTransition (which tells the spatial server itself).
    private static void EndLease(LiveItem record, string reason) =>
        ServerItemManager.Instance?.HostSpatial.EndLease(record.NetId, reason);
    public static void Clear()
    {
        records.Clear();
        ItemChangeLog.RecentRemovals.Clear();
    }
    public static IEnumerable<LiveItem> Records => records.Values;

    /// <summary>
    /// A backstop for the server's own player taking another player's item. In playtest 2 items
    /// in a VR player's hand or inventory (lanterns as they were lit, an EOT lantern) ended up
    /// recorded in the server's player's hand, though the server's copy still belonged to the
    /// player; the next update moved them to the server's parked body and they vanished from the
    /// player's hand. Not every path that did it was found, so whatever the transition, a record
    /// that lands on the server's player while its item is another present player's goes back.
    /// </summary>
    private static void KeepRemotePossession(NetworkedItem item, LiveItem record, ItemUpdate snapshot)
    {
        if (record.Placement is not (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory))
            return;
        var server = NetworkServer.Instance;
        if (server?.LocalPlayer == null || record.Holder != server.LocalPlayer.Guid)
            return;
        // The host's game knows who holds its copy only by number.
        byte possessor = item.playerBelongsToId;
        if (possessor == 0 || possessor == server.SelfId || !server.TryGetServerPlayer(possessor, out ServerPlayer holder))
            return;
        record.Holder = holder.Guid;
        if (snapshot != null && snapshot.PlayerId == server.SelfId)
            snapshot.PlayerId = possessor;
        Multiplayer.LogWarning($"Item {item.NetId} ({record.PrefabName}) was recorded as the server's player's, " +
                               $"but it's {holder.Username}'s; kept it theirs");
    }

    private static LiveItemState ToCore(LiveItem record)
    {
        return new LiveItemState
        {
            NetId = record.NetId,
            Revision = record.Revision,
            Placement = (LivePlacement)(byte)record.Placement,
            Holder = record.Holder,
            Owner = record.Owner,
            InventoryClaimSlot = record.InventoryClaimSlot,
            InventoryClaimFlags = (ClaimFlags)(byte)record.InventoryClaimFlags,
            ObjectStateRunBase = record.ObjectStateRunBase,
            ObjectStateRunTop = record.ObjectStateRunTop,
            SettledBy = record.SettledBy,
            SettledBase = record.SettledBase,
            SettledTop = record.SettledTop
        };
    }

    private static void ApplyCore(LiveItem record, LiveItemState state)
    {
        record.Revision = state.Revision;
        record.Placement = (ItemPlacementKind)(byte)state.Placement;
        record.Holder = state.Holder;
        record.PlacementHand = PlayerHand.None;
        // The owner and the claim slot are the ledger's: a commit changes them, not this.
        record.InventoryClaimFlags = (ItemInventoryClaimFlags)(byte)state.InventoryClaimFlags;
        record.ObjectStateRunBase = state.ObjectStateRunBase;
        record.ObjectStateRunTop = state.ObjectStateRunTop;
        record.SettledBy = state.SettledBy;
        record.SettledBase = state.SettledBase;
        record.SettledTop = state.SettledTop;
    }

    private static ItemPlacementKind PlacementFrom(ItemState state) => state switch
    {
        ItemState.InHand => ItemPlacementKind.PlayerHand,
        ItemState.InInventory => ItemPlacementKind.PlayerInventory,
        ItemState.Attached => ItemPlacementKind.Attached,
        ItemState.Installed => ItemPlacementKind.Installed,
        ItemState.InMagazine => ItemPlacementKind.Magazine,
        _ => ItemPlacementKind.World
    };

    private static bool HasRetrievalFlag(ItemInventoryClaimFlags flags) =>
        (flags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;

    private static void PublishRejected(NetworkedItem item, LiveItem record, ServerPlayer actor,
        ItemUpdate snapshot, string reason)
    {
        Publish("item.transition-rejected", item, record, new()
        {
            ["actorPlayerId"] = actor?.PlayerId ?? 0,
            ["requestedState"] = snapshot.ItemState.ToString(),
            ["requestedRevision"] = snapshot.AuthorityRevision,
            ["rejectionReason"] = reason
        }, DebugSeverity.Warning);
    }

    private static void Publish(string eventName, NetworkedItem item, LiveItem record,
        Dictionary<string, object> additional, DebugSeverity severity = DebugSeverity.Info)
    {
        if (!DebugRuntime.EnabledFor("item") || item == null)
            return;
        Dictionary<string, object> data = Snapshot(record);
        if (additional != null)
            foreach (KeyValuePair<string, object> pair in additional)
                data[pair.Key] = pair.Value;
        DebugRuntime.Publish("item", eventName, DebugRuntimeSide.Server, severity,
            "Item", item.NetId.ToString(), data);
    }
}
