// Captures this peer's actual Unity state for everything networked, for the desync checker.
// Runs identically on host and client. Positions are absolute (origin shift removed) or
// local to the car an item rides in, so they are comparable across peers.
using DV.InventorySystem;
using DV.ThingTypes;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;

public static class Probe
{
    public static object Run(DevContext ctx)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        bool isHost = lifecycle != null && lifecycle.IsServerRunning;

        return new
        {
            role = ctx.Role,
            t = Time.realtimeSinceStartup,
            tick = lifecycle != null ? lifecycle.Tick : 0u,
            me = lifecycle?.Client != null ? new { id = lifecycle.Client.PlayerId, name = lifecycle.Client.Username } : null,
            player = V(Dev.PlayerAbs),
            items = Items(),
            cars = Cars(),
            world = World(),
            interest = isHost ? Interest(lifecycle) : null
        };
    }

    private static List<object> Items()
    {
        List<object> list = new();
        foreach (NetworkedItem item in Dev.All<NetworkedItem>())
        {
            if (item == null || item.NetId == 0) continue;
            Transform t = item.transform;
            Rigidbody rb = item.Item != null ? item.Item.ItemRigidbody : null;

            ItemState state;
            try { state = item.GetItemState(); } catch { state = (ItemState)255; }

            string parent;
            float[] pos;
            TrainCar car = TrainCar.Resolve(t);
            if (state is ItemState.InHand or ItemState.InInventory)
            {
                // Held/inventory items live in different hierarchies per peer (vanilla keeps the
                // local inventory unparented; the host parks remote inventories under the avatar).
                parent = "player";
                pos = null;
            }
            else if (car != null && NetworkedTrainCar.TryGetNetId(car, out ushort carNetId))
            {
                parent = "car:" + carNetId;
                pos = V(car.transform.InverseTransformPoint(t.position));
            }
            else if (t.GetComponentInParent<NetworkedPlayer>() != null || PlayerManager.PlayerTransform != null && t.IsChildOf(PlayerManager.PlayerTransform))
            {
                parent = "player";
                pos = null; // hand/inventory poses are presentation, not state
            }
            else
            {
                parent = t.parent == null ? "root" : t.parent.name == "origin_shift_parent" ? "world" : "other:" + t.parent.name;
                pos = V(Dev.Abs(t.position));
            }

            list.Add(new
            {
                id = item.NetId,
                n = item.name.Replace("(Clone)", string.Empty),
                a = item.gameObject.activeInHierarchy,
                // Distance from this peer's own player: vanilla ItemDisabler hides items more
                // than a 128 m grid cell away, so visibility only means something up close.
                d = PlayerManager.PlayerTransform != null ? (int)Vector3.Distance(t.position, PlayerManager.PlayerTransform.position) : -1,
                s = state.ToString(),
                rev = item.AuthorityRevision,
                p = parent,
                pos,
                k = rb != null ? (bool?)rb.isKinematic : null,
                sl = rb != null && rb.IsSleeping(),
                v = rb != null && !rb.isKinematic ? (float)Math.Round(rb.velocity.magnitude, 2) : 0f
            });
        }
        return list;
    }

    private static List<object> Cars()
    {
        List<object> list = new();
        foreach (NetworkedTrainCar net in Dev.All<NetworkedTrainCar>())
        {
            TrainCar car = net != null ? net.TrainCar : null;
            if (car == null || net.NetId == 0) continue;
            list.Add(new
            {
                id = net.NetId,
                cid = car.ID,
                a = car.gameObject.activeInHierarchy,
                pos = V(Dev.Abs(car.transform.position)),
                spd = (float)Math.Round(car.GetForwardSpeed(), 2),
                der = car.derailed,
                cf = Neighbour(car.frontCoupler),
                cr = Neighbour(car.rearCoupler),
                bogies = car.Bogies?.Select(b => b.track != null ? b.track.name + "@" + Math.Round(b.traveller?.Span ?? -1, 1) : "none").ToArray(),
                bp = car.brakeSystem != null ? (float?)Math.Round(car.brakeSystem.brakePipePressure, 2) : null,
                hb = car.brakeSystem != null ? (float?)Math.Round(car.brakeSystem.handbrakePosition, 2) : null
            });
        }
        return list;
    }

    private static int Neighbour(Coupler coupler)
    {
        TrainCar other = coupler?.coupledTo?.train;
        return other != null && NetworkedTrainCar.TryGetNetId(other, out ushort id) ? id : 0;
    }

    private static object World()
    {
        List<int> branches = new();
        foreach (Junction junction in Dev.All<Junction>().OrderBy(j => j.junctionData.junctionId))
            branches.Add(junction.junctionData.junctionId * 10 + junction.selectedBranch);
        return new
        {
            money = Inventory.Instance != null ? Math.Round(Inventory.Instance.PlayerMoney, 2) : -1,
            junctions = branches
        };
    }

    // What the host believes each remote player has been sent and still holds.
    private static object Interest(NetworkLifecycle lifecycle)
    {
        return lifecycle.Server.ServerPlayers
            .Where(p => p.PlayerId != lifecycle.Client?.PlayerId)
            .Select(p => new
            {
                id = p.PlayerId,
                name = p.Username,
                known = p.KnownItems.Keys.Where(i => i != null).Select(i => (int)i.NetId).OrderBy(i => i).ToArray()
            })
            .ToList();
    }

    private static float[] V(Vector3? v) => v.HasValue
        ? new[] { (float)Math.Round(v.Value.x, 3), (float)Math.Round(v.Value.y, 3), (float)Math.Round(v.Value.z, 3) }
        : null;
}
