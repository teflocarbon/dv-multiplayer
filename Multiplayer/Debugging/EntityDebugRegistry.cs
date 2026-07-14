using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data.Items;
using DV;
using DV.InventorySystem;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Debugging;

public static class EntityDebugRegistry
{
    private sealed class Record
    {
        public string Type;
        public string Id;
        public string DisplayName;
        public WeakReference Reference;
        public DebugSeverity Severity;
        public DateTime UpdatedUtc;
        public float NextLiveRefresh;
        public Dictionary<string, object> State = new(StringComparer.Ordinal);
        public LinkedList<DebugEvent> Timeline = new();
    }

    private static readonly object gate = new();
    private static readonly Dictionary<string, Record> records = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> traced = new(StringComparer.OrdinalIgnoreCase);
    private static int timelineCapacity = 250;
    private static float nextReconcile;
    private static int reconcilePhase;
    private static int itemReconcileOffset;
    private static bool running;

    public static void Start(DebugEventStore store, int maxTimeline)
    {
        timelineCapacity = Mathf.Clamp(maxTimeline, 50, 2000);
        running = true;
        nextReconcile = 0;
    }

    public static void Stop() { lock (gate) { running = false; records.Clear(); traced.Clear(); } }
    public static void Resize(int value) { lock (gate) { timelineCapacity = Mathf.Clamp(value, 50, 2000); foreach (Record record in records.Values) Trim(record); } }
    public static bool IsTraced(string type, string id) => !string.IsNullOrEmpty(id) && traced.Contains(Key(type, id));
    public static void Trace(string type, string id, bool enabled) { lock (gate) { if (enabled) traced.Add(Key(type, id)); else traced.Remove(Key(type, id)); } }

    public static void Register(string type, string id, string displayName, Object unityObject, Dictionary<string, object> state = null)
    {
        if (!running || string.IsNullOrWhiteSpace(id)) return;
        lock (gate)
        {
            string key = Key(type, id);
            if (!records.TryGetValue(key, out Record record)) records[key] = record = new Record { Type = type, Id = id };
            record.DisplayName = displayName ?? id;
            record.Reference = unityObject == null ? null : new WeakReference(unityObject);
            record.UpdatedUtc = DateTime.UtcNow;
            if (state != null) record.State = state;
        }
    }

    public static void Unregister(string type, string id)
    {
        if (!running) return;
        lock (gate)
        {
            if (records.TryGetValue(Key(type, id), out Record record))
            {
                record.Reference = null;
                record.State["destroyed"] = true;
                record.UpdatedUtc = DateTime.UtcNow;
            }
        }
    }

    public static void UpdateState(string type, string id, Dictionary<string, object> state, DebugSeverity severity = DebugSeverity.Info)
    {
        if (!running || string.IsNullOrWhiteSpace(id)) return;
        lock (gate)
        {
            if (!records.TryGetValue(Key(type, id), out Record record)) records[Key(type, id)] = record = new Record { Type = type, Id = id, DisplayName = id };
            record.State = state ?? new Dictionary<string, object>(StringComparer.Ordinal);
            record.Severity = severity;
            record.UpdatedUtc = DateTime.UtcNow;
        }
    }

    public static void Observe(DebugEvent item)
    {
        if (!running || string.IsNullOrEmpty(item?.EntityId)) return;
        lock (gate)
        {
            string key = Key(item.EntityType, item.EntityId);
            if (!records.TryGetValue(key, out Record record)) records[key] = record = new Record { Type = item.EntityType, Id = item.EntityId, DisplayName = item.EntityId };
            record.Timeline.AddLast(item);
            record.UpdatedUtc = item.TimestampUtc;
            if (item.Severity > record.Severity) record.Severity = item.Severity;
            Trim(record);
        }
    }

    public static IEnumerable<DebugEntityDto> Snapshot()
    {
        lock (gate) return records.Values.Select(record => ToDto(record)).OrderBy(item => item.EntityType).ThenBy(item => item.EntityId).ToArray();
    }

    public static IEnumerable<DebugEntityDto> Summaries()
    {
        lock (gate) return records.Values.Select(record => ToDto(record, false, false)).OrderBy(item => item.EntityType).ThenBy(item => item.EntityId).ToArray();
    }

    public static DebugEntityDto Get(string type, string id)
    {
        lock (gate) return records.TryGetValue(Key(type, id), out Record record) ? ToDto(record) : null;
    }

    public static DebugEntityDto GetLabel(string type, string id)
    {
        lock (gate)
        {
            if (!records.TryGetValue(Key(type, id), out Record record)) return null;
            DebugEntityDto result = ToDto(record, false, true);
            if (record.Timeline.Last != null) result.Timeline.Add(record.Timeline.Last.Value);
            return result;
        }
    }

    /// <summary>
    /// Refreshes one registry record from its live Unity component. This must only be called
    /// from Unity's main thread. The per-record throttle lets the overlay and world labels
    /// request the same entity without duplicating snapshot work.
    /// </summary>
    public static bool RefreshLiveState(string type, string id, float minimumInterval = 0.2f)
    {
        if (!running || string.IsNullOrWhiteSpace(id)) return false;
        Object target;
        lock (gate)
        {
            if (!records.TryGetValue(Key(type, id), out Record record) || record.Reference?.Target is not Object value || value == null) return false;
            if (Time.unscaledTime < record.NextLiveRefresh) return false;
            record.NextLiveRefresh = Time.unscaledTime + Mathf.Max(0.1f, minimumInterval);
            target = value;
        }

        try
        {
            switch (target)
            {
                case NetworkedItem item:
                    RegisterItem(item);
                    break;
                case NetworkedPlayer player:
                    RegisterPlayer(player);
                    break;
                case NetworkedTrainCar car:
                    RegisterTrain(car);
                    break;
                case Transform transform when type == "Player" && transform == PlayerManager.PlayerTransform:
                    RegisterLocalPlayer();
                    break;
                case Component component:
                    Register(type, id, component.name, component, CommonState(component));
                    break;
                default:
                    return false;
            }
            return true;
        }
        catch (MissingReferenceException)
        {
            Unregister(type, id);
            return false;
        }
        catch (NullReferenceException)
        {
            // Components can be torn down between the weak-reference check and snapshot.
            return false;
        }
    }

    internal static IEnumerable<(DebugEntityDto dto, Component component)> Live()
    {
        lock (gate)
        {
            return records.Values.Select(record => (ToDto(record, false, false), record.Reference?.Target as Component))
                .Where(item => item.Item2 != null).ToArray();
        }
    }

    public static void Tick()
    {
        if (!running || Time.unscaledTime < nextReconcile) return;
        nextReconcile = Time.unscaledTime + 2f;
        RegisterLocalPlayer();
        switch (reconcilePhase++ % 5)
        {
            case 0:
                NetworkedItem[] items = NetworkedItem.GetAll().Where(item => item != null).ToArray();
                int itemBudget = Math.Min(32, items.Length);
                for (int index = 0; index < itemBudget; index++) RegisterItem(items[(itemReconcileOffset + index) % items.Length]);
                if (items.Length > 0) itemReconcileOffset = (itemReconcileOffset + itemBudget) % items.Length;
                break;
            case 1:
                foreach (NetworkedPlayer player in Object.FindObjectsOfType<NetworkedPlayer>()) RegisterPlayer(player);
                break;
            case 2:
                foreach (NetworkedTrainCar car in Object.FindObjectsOfType<NetworkedTrainCar>()) RegisterTrain(car);
                break;
            case 3:
                foreach (Trainset set in Trainset.allSets.Where(set => set?.cars != null && set.cars.Count > 0))
                {
                    TrainCar first = set.firstCar ?? set.cars.FirstOrDefault();
                    Dictionary<string, object> state = new()
                    {
                        ["setId"] = set.id,
                        ["carIds"] = set.cars.Where(car => car != null).Select(car => (object)car.ID).ToList(),
                        ["firstCar"] = set.firstCar?.ID ?? string.Empty,
                        ["lastCar"] = set.lastCar?.ID ?? string.Empty
                    };
                    string setId = set.id.ToString();
                    Register("Trainset", setId, $"Trainset {setId}", first, state);
                }
                break;
            default:
                foreach (NetworkedJob job in Object.FindObjectsOfType<NetworkedJob>()) Register("Job", job.NetId.ToString(), job.Job?.ID ?? $"Job {job.NetId}", job, CommonState(job));
                foreach (NetworkedStationController station in Object.FindObjectsOfType<NetworkedStationController>()) Register("Station", station.NetId.ToString(), station.name, station, CommonState(station));
                break;
        }
    }

    public static void RegisterItem(NetworkedItem item)
    {
        Register("Item", item.NetId.ToString(), item.Item?.InventorySpecs?.ItemPrefabName ?? item.name, item, ItemState(item));
    }

    public static void RegisterPlayer(NetworkedPlayer player)
    {
        Dictionary<string, object> state = CommonState(player);
        state["username"] = player.Username;
        state["isVr"] = player.IsVR;
        state["isOnCar"] = player.IsOnCar;
        state["car"] = player.OccupiedCar?.ID ?? string.Empty;
        Register("Player", player.PlayerId.ToString(), player.DisplayName, player, state);
    }

    public static void RegisterLocalPlayer()
    {
        Transform transform = PlayerManager.PlayerTransform;
        DebugSessionInfo session = DebugRuntime.Session;
        if (transform == null || session == null || !session.PlayerId.HasValue) return;
        Dictionary<string, object> state = CommonState(transform);
        state["username"] = string.IsNullOrWhiteSpace(session.PlayerName) ? Multiplayer.Settings.GetUserName() : session.PlayerName;
        state["isLocal"] = true;
        state["isVr"] = VRManager.IsVREnabled();
        state["isOnCar"] = PlayerManager.Car != null;
        state["car"] = PlayerManager.Car?.ID ?? string.Empty;
        state["cameraPosition"] = DebugValueSnapshotter.Snapshot(PlayerManager.ActiveCamera?.transform.position);
        state["worldOrigin"] = DebugValueSnapshotter.Snapshot(WorldMover.currentMove);
        string id = session.PlayerId.Value.ToString();
        Register("Player", id, $"{state["username"]} (local)", transform, state);
    }

    public static void RegisterTrain(NetworkedTrainCar car)
    {
        Dictionary<string, object> state = CommonState(car);
        state["carId"] = car.CurrentID ?? string.Empty;
        state["derailed"] = car.TrainCar?.derailed ?? false;
        state["velocity"] = DebugValueSnapshotter.Snapshot(car.TrainCar?.rb?.velocity);
        state["lastPhysicsTick"] = car.lastTickProcessed;
        Register("TrainCar", car.NetId.ToString(), car.CurrentID ?? $"Car {car.NetId}", car, state);
    }

    public static Dictionary<string, object> ItemState(NetworkedItem item)
    {
        Dictionary<string, object> state = CommonState(item);
        state["prefabName"] = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name;
        state["itemState"] = item.DebugCurrentState.ToString();
        state["unityInstanceId"] = item.gameObject.GetInstanceID();
        state["activeSelf"] = item.gameObject.activeSelf;
        state["activeInHierarchy"] = item.gameObject.activeInHierarchy;
        state["parent"] = item.transform.parent?.name ?? string.Empty;
        state["holderPlayerId"] = item.playerBelongsToId;
        state["hostHolder"] = item.BelongsTo?.PlayerId ?? 0;
        state["authorityRevision"] = item.AuthorityRevision;
        state["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId;
        state["inventoryClaimPlayerId"] = item.InventoryClaimPlayerId;
        state["inventoryClaimSlot"] = item.InventoryClaimSlot;
        state["inventoryClaimFlags"] = item.InventoryClaimFlags.ToString();
        if (AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record authorityRecord))
            state["canonicalAuthority"] = AuthoritativeItemRegistry.Snapshot(authorityRecord);
        state["transitionReason"] = item.LastTransitionReason.ToString();
        state["foreignOwned"] = item.IsForeignOwned;
        state["unboundState"] = item.UnboundState.ToString();
        state["velocity"] = DebugValueSnapshotter.Snapshot(item.Item?.ItemRigidbody?.velocity);
        state["angularVelocity"] = DebugValueSnapshotter.Snapshot(item.Item?.ItemRigidbody?.angularVelocity);
        try
        {
            Inventory inventory = Inventory.Instance;
            int inventorySlot = inventory?.IndexOf(item.gameObject) ?? -1;
            state["inventorySlot"] = inventorySlot;
            state["equippedSlot"] = inventory?.GetEquipSlotForItem(item.gameObject) ?? -1;
            state["inventoryContainsActive"] = inventory?.Contains(item.gameObject, false) ?? false;
            state["inventoryContainsIncludingDropped"] = inventory?.Contains(item.gameObject, true) ?? false;
            state["inventorySlotReserved"] = inventorySlot >= 0 && inventory.GetSlotReservedState(inventorySlot);
            state["inventorySlotDropped"] = inventorySlot >= 0 && inventory.GetSlotDroppedState(inventorySlot);
            state["inventorySlotLocked"] = inventorySlot >= 0 && inventory.GetSlotLockState(inventorySlot);
            state["isEssential"] = item.Item?.IsEssential() ?? false;
            state["belongsToPlayerSpec"] = item.Item?.InventorySpecs?.BelongsToPlayer ?? false;
            state["claimSlotMatchesLocalSlot"] = inventorySlot >= 0 && inventorySlot == item.InventoryClaimSlot;
            state["claimStolen"] = item.InventoryClaimFlags.HasFlag(ItemInventoryClaimFlags.Stolen);
            state["inInventoryStorage"] = StorageController.Instance?.StorageInventory?.ContainsItem(item.Item) ?? false;
            state["inWorldStorage"] = StorageController.Instance?.StorageWorld?.ContainsItem(item.Item) ?? false;
            state["inLostAndFound"] = StorageController.Instance?.StorageLostAndFound?.ContainsItem(item.Item) ?? false;
        }
        catch { }
        return state;
    }

    private static Dictionary<string, object> CommonState(Component component) => new()
    {
        ["name"] = component.name,
        ["scene"] = component.gameObject.scene.name,
        ["gameObjectPath"] = component.transform.GetDebugPath(),
        ["positionLocal"] = DebugValueSnapshotter.Snapshot(component.transform.position),
        ["positionAbsolute"] = DebugValueSnapshotter.Snapshot(component.transform.position - WorldMover.currentMove),
        ["rotation"] = DebugValueSnapshotter.Snapshot(component.transform.rotation),
        ["active"] = component.gameObject.activeInHierarchy
    };

    private static DebugEntityDto ToDto(Record record, bool includeTimeline = true, bool includeState = true) => new()
    {
        EntityType = record.Type, EntityId = record.Id, DisplayName = record.DisplayName,
        Severity = record.Severity, LastUpdatedUtc = record.UpdatedUtc,
        LatestState = includeState ? new Dictionary<string, object>(record.State, StringComparer.Ordinal) : new Dictionary<string, object>(StringComparer.Ordinal),
        Timeline = includeTimeline ? record.Timeline.ToList() : new List<DebugEvent>()
    };
    private static void Trim(Record record) { while (record.Timeline.Count > timelineCapacity) record.Timeline.RemoveFirst(); }
    private static string Key(string type, string id) => $"{type ?? string.Empty}:{id ?? string.Empty}";

    private static string GetDebugPath(this Transform transform)
    {
        if (transform == null) return string.Empty;
        Stack<string> parts = new();
        for (Transform current = transform; current != null; current = current.parent) parts.Push(current.name);
        return string.Join("/", parts);
    }
}
