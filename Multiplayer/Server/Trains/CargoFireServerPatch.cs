using System.Collections.Generic;
using System.Linq;
using DV.Hazmat;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Transport;
using UnityEngine;

namespace Multiplayer.Server.Trains;

/// <summary>
/// The host's cargo fires, sent to the players as they change (see <see cref="CargoFireSync"/>), and
/// the players' requests to light one.
/// </summary>
public static class CargoFireServer
{
    // The state last sent for each reaction.
    private static readonly Dictionary<CargoReactionBase, CargoFireSync.FireState> sent = new();

    // A lighter has to touch the leak; a flare can be thrown at it.
    private const float MaxIgniteDistance = 60f;

    internal static void AfterUpdate(CargoReactionBase reaction)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsHost() || lifecycle.Server == null)
            return;
        CargoFireSync.FireState state = CargoFireSync.State(reaction);
        if (sent.TryGetValue(reaction, out CargoFireSync.FireState last) ? last == state : state == CargoFireSync.FireState.None)
            return;
        sent[reaction] = state;
        if (CargoFireSync.TryLocate(reaction, out NetworkedTrainCar networked, out byte index))
            lifecycle.Server.SendCargoFire(networked.NetId, index, state);
    }

    // A joining player gets every car that's burning now.
    public static void SendAll(ITransportPeer peer)
    {
        foreach (CargoReactionBase reaction in sent.Keys.Where(r => r == null).ToList())
            sent.Remove(reaction);
        foreach (KeyValuePair<CargoReactionBase, CargoFireSync.FireState> kvp in sent)
            if (kvp.Value != CargoFireSync.FireState.None && CargoFireSync.TryLocate(kvp.Key, out NetworkedTrainCar networked, out byte index))
                NetworkLifecycle.Instance.Server.SendCargoFire(networked.NetId, index, kvp.Value, peer);
    }

    public static void Ignite(TrainCar car, byte index, float strength, bool explode, Vector3 playerPosition, string username)
    {
        if (car == null || (playerPosition - car.transform.position).sqrMagnitude > MaxIgniteDistance * MaxIgniteDistance)
        {
            Multiplayer.LogDebug(() => $"{username} tried to light {car?.ID} from too far away");
            return;
        }
        CargoReactionBase[] reactions = ExplosionSync.CargoReactions(car);
        if (index >= reactions.Length)
            return;
        ICargoReaction reaction = reactions[index];
        if (explode)
            reaction.TryExplodeExternally();
        else
            reaction.TryIgniteExternally(Mathf.Clamp(strength, 0f, 1000f));
    }

    public static void Clear() => sent.Clear();
}

[HarmonyPatch(typeof(CargoReactionBase), "Update")]
public static class CargoReactionUpdateServerPatch
{
    private static void Postfix(CargoReactionBase __instance) => CargoFireServer.AfterUpdate(__instance);
}
