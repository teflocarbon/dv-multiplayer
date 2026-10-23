// Gadget steps and helpers: test gadget items, installing one the way vanilla does, and reading the
// server's record of an installed gadget. Each [Step] runs inside a game: call it through pair.Host /
// pair.Client. Helpers without [Step] are for other steps.
using System;
using System.Collections;
using System.Linq;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>The server's record of a gadget item, found by its persistent id.</summary>
    public sealed class GadgetRecord
    {
        /// <summary>How many live items have that persistent id (1, unless it was duplicated).</summary>
        public int Count;
        public ushort NetId;
        public string Placement;
        /// <summary>The car its record names, by GUID, for an installed one.</summary>
        public string CarGuid;
        public string OwnerGuid;
        /// <summary>Vanilla holds it in its installed storage, its projection linked.</summary>
        public bool InstalledInVanilla;
        /// <summary>The anchor its projection is linked to (a CarGUID or a singleton key), or "".</summary>
        public string Anchor;
        public bool Active;
        public bool InLostAndFound;
    }

    public static class Gadgets
    {
        /// <summary>
        /// A gadget that installs on its own: no mount needed, not a mount, not a label (labels replace
        /// themselves when taken off), nothing hangs on it. The same one every run.
        /// </summary>
        public static InventoryItemSpec SimpleSpec() =>
            DV.Globals.G.Items.items
                .Where(spec => spec != null && spec.GetComponent<GadgetItem>()?.gadgetPrefab is { } g &&
                               g.RequiredMountPoints == 0 && g.GetComponent<Mount>() == null &&
                               g.GetComponent<DestroyOnGadgetUnlink>() == null && g.GetComponent<TextGadget>() == null &&
                               g.GetComponent<GadgetWithSnapPoint>() == null)
                .OrderBy(spec => spec.ItemPrefabName, StringComparer.Ordinal)
                .FirstOrDefault() ?? throw new InvalidOperationException("no gadget installs without a mount");

        /// <summary>The first connected player who isn't the host's own.</summary>
        public static ServerPlayer Client()
        {
            NetworkServer server = NetworkServer.Instance;
            return server.ServerPlayers.First(player => player.PlayerId != server.SelfId);
        }

        /// <summary>
        /// Host: a gadget item of <paramref name="spec"/> at <paramref name="at"/>, owned by
        /// <paramref name="owner"/> as a purchase of theirs would be. Yields until it has a record.
        /// </summary>
        public static IEnumerator SpawnOwned(InventoryItemSpec spec, Vector3 at, ServerPlayer owner, Action<NetworkedItem> got)
        {
            GameObject go = UnityEngine.Object.Instantiate(spec.gameObject, at, Quaternion.identity, WorldMover.OriginShiftParent);
            go.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
            NetworkedItem item = go.GetComponent<NetworkedItem>();
            AuthoritativeItemRegistry.Record record = null;
            yield return Wait.Until(() => item != null && item.NetId != 0 &&
                (record = AuthoritativeItemRegistry.Ensure(item)) != null, 10f, $"the {spec.ItemPrefabName}'s record");
            record.PersistentOwnerIdentity = owner.Guid;
            record.PersistentOwnerPlayerId = owner.PlayerId;
            owner.AddOwnedItem(item.NetId);
            // GadgetItem.Item is set in Start.
            for (int i = 0; i < 10; i++) yield return null;
            got(item);
        }

        /// <summary>Installs the item on <paramref name="anchor"/> at <paramref name="worldPosition"/>, as
        /// vanilla does once its checks pass (GadgetItem.Place).</summary>
        public static GadgetBase Install(NetworkedItem item, Customization anchor, Vector3 worldPosition)
        {
            Transform parent = anchor.GetParentingTransform();
            return GadgetItem.Place(anchor, parent.InverseTransformPoint(worldPosition), Quaternion.identity,
                item.GetComponent<GadgetItem>());
        }

        /// <summary>Host: what the server knows of the item with this persistent id.</summary>
        [Step]
        public static GadgetRecord Record(string persistentId)
        {
            Guid id = Guid.Parse(persistentId);
            var records = AuthoritativeItemRegistry.GetAll().Where(r => r.PersistentItemId == id).ToList();
            var result = new GadgetRecord { Count = records.Count };
            if (records.Count == 0)
                return result;
            AuthoritativeItemRegistry.Record record = records[0];
            NetworkedItem.TryGet(record.NetId, out NetworkedItem item);
            GadgetBase gadget = item?.GetComponent<GadgetItem>()?.Gadget;
            result.NetId = record.NetId;
            result.Placement = record.Placement.ToString();
            result.CarGuid = record.Placement == ItemPlacementKind.Installed ? record.AttachedCarPersistentId : "";
            result.OwnerGuid = record.PersistentOwnerIdentity == Guid.Empty ? "" : record.PersistentOwnerIdentity.ToString("D");
            result.InstalledInVanilla = item != null && Multiplayer.Server.Gadgets.GadgetItemTransitions.IsInstalled(item);
            result.Anchor = gadget != null && gadget.Custom != null ? gadget.Custom.GetIdentificationKey() : "";
            result.Active = item != null && item.gameObject.activeInHierarchy;
            result.InLostAndFound = NetworkedLostAndFoundManager.Contains(record.NetId);
            return result;
        }

        /// <summary>Host: destroys the items with these persistent ids, wherever they are, projections too.</summary>
        [Step]
        public static IEnumerator DestroyByPersistentId(string[] persistentIds)
        {
            foreach (string persistentId in persistentIds)
            {
                Guid id = Guid.Parse(persistentId);
                foreach (var record in AuthoritativeItemRegistry.GetAll().Where(r => r.PersistentItemId == id).ToList())
                {
                    if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
                        continue;
                    GadgetBase gadget = item.GetComponent<GadgetItem>()?.Gadget;
                    if (gadget != null && gadget.IsLinked)
                        gadget.ForceRemove(reparentToTrainCar: false);
                    yield return null;
                    if (gadget != null)
                        UnityEngine.Object.Destroy(gadget.gameObject);
                    UnityEngine.Object.Destroy(item.gameObject);
                }
            }
            for (int i = 0; i < 10; i++) yield return null;
        }

        /// <summary>
        /// The gadgets installed on <paramref name="car"/>, one line each in UID order, comparable
        /// between games: prefab, UID, pose (cm), power, and the gadget's own vanilla save data
        /// (soldering, wires, mount points, the mount, settings) with its wire list sorted, since
        /// wiring order can differ between games while the wires are the same. Then the car's holes.
        /// </summary>
        [Step]
        public static string Digest(string car)
        {
            TrainCar trainCar = CarSpawner.Instance.AllCars.First(c => c != null && c.ID == car);
            var lines = trainCar.Customization.Customizers.OfType<GadgetBase>().OrderBy(g => g.UID).Select(g =>
            {
                var data = new Newtonsoft.Json.Linq.JObject();
                g.SaveDataRequested(data);
                if (data["links"] is Newtonsoft.Json.Linq.JArray links)
                    data["links"] = new Newtonsoft.Json.Linq.JArray(links.Select(l => (int)l).OrderBy(l => l));
                Vector3 p = g.transform.localPosition * 100f;
                string prefab = g.GadgetItem?.GetComponent<InventoryItemSpec>()?.ItemPrefabName ?? g.name;
                return $"{prefab} uid={g.UID} at=({Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.y)},{Mathf.RoundToInt(p.z)}) " +
                       $"power={g.PowerState} {data.ToString(Newtonsoft.Json.Formatting.None)}";
            });
            return string.Join("\n", lines) + $"\nholes={trainCar.Customization.Holes.Count()}";
        }

        /// <summary>This game's <see cref="Digest"/> once it matches <paramref name="expected"/>, or after
        /// <paramref name="seconds"/>, whichever is first.</summary>
        [Step]
        public static IEnumerator DigestWhenEqual(Step<string> step, string car, string expected, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            do
            {
                step.Result = CarSpawner.Instance.AllCars.Any(c => c != null && c.ID == car) ? Digest(car) : "(no car " + car + ")";
                if (step.Result == expected)
                    yield break;
                yield return new WaitForSecondsRealtime(0.5f);
            } while (Time.realtimeSinceStartup < end);
        }

        /// <summary>
        /// Of the items with these net IDs, how many this game has as installed gadgets: the item
        /// inactive, its projection linked to a car.
        /// </summary>
        [Step]
        public static int CountInstalled(ushort[] netIds) =>
            netIds.Count(id => NetworkedItem.TryGet(id, out NetworkedItem item) && item != null &&
                               !item.gameObject.activeSelf && item.GetComponent<GadgetItem>()?.Gadget?.IsLinked == true);

        /// <summary>Whether this game has the item active anywhere after <paramref name="seconds"/>.</summary>
        [Step]
        public static IEnumerator ActiveHere(Step<bool> step, ushort netId, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            step.Result = NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
                          item.gameObject.activeInHierarchy;
        }
    }
}
