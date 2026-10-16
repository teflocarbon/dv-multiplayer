using System;
using System.Collections.Generic;
using System.Linq;
using DV.Hazmat;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Patches.Train;

/// <summary>
/// A car's cargo fire is the host's, like its explosion (<see cref="ExplosionSync"/>). Each game
/// used to run its own: a leak lit with a lighter, ground fire spreading to a car, a car catching
/// on its own (a random roll every frame) or going out all happened on one screen only, so a car
/// could burn for one player and never for anyone else. A client's lit car couldn't explode either:
/// its countdown is blocked, and the host's car wasn't burning.
///
/// Now the host sends each car's fire state (burning, about to explode) when it changes, and
/// clients hold their copy to it. A client's lighter or flare at a car asks the host, which lights
/// its own copy if the player is close enough. Explosions themselves stay with ExplosionSync.
/// </summary>
public static class CargoFireSync
{
    [Flags]
    public enum FireState : byte { None = 0, Ignited = 1, AboutToExplode = 2 }

    // Host: the state last sent for each reaction. Client: the host's state for each reaction.
    private static readonly Dictionary<CargoReactionBase, FireState> sent = new();
    private static readonly Dictionary<CargoReactionBase, FireState> hostState = new();

    // Set while an item's igniter (a lighter or flare) runs: only those ignitions come from a player.
    // The rest on a client are its replay of what the host does anyway (an explosion igniting the
    // cars around it).
    internal static bool LocalIgniter;

    // A lighter has to touch the leak; a flare can be thrown at it.
    private const float MaxIgniteDistance = 60f;

    private static bool IsClient => ExplosionSync.IsClient;

    private static bool IsHostWithPlayers =>
        NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsHost() && NetworkLifecycle.Instance.Server != null;

    private static FireState State(CargoReactionBase reaction) =>
        (reaction.isIgnited ? FireState.Ignited : FireState.None) | (reaction.aboutToExplode ? FireState.AboutToExplode : FireState.None);

    private static bool TryLocate(CargoReactionBase reaction, out NetworkedTrainCar networked, out byte index)
    {
        networked = null;
        index = 0;
        TrainCar car = ExplosionSync.ReactionCar(reaction);
        if (car == null || !car.TryNetworked(out networked))
            return false;
        int i = Array.IndexOf(ExplosionSync.CargoReactions(car), reaction);
        if (i < 0 || i > byte.MaxValue)
            return false;
        index = (byte)i;
        return true;
    }

    // ---------------------------------------------------------------- host

    internal static void Host_AfterUpdate(CargoReactionBase reaction)
    {
        if (!IsHostWithPlayers)
            return;
        FireState state = State(reaction);
        if (sent.TryGetValue(reaction, out FireState last) ? last == state : state == FireState.None)
            return;
        sent[reaction] = state;
        if (TryLocate(reaction, out NetworkedTrainCar networked, out byte index))
            NetworkLifecycle.Instance.Server.SendCargoFire(networked.NetId, index, state);
    }

    // A joining player gets every car that's burning now.
    public static void Server_SendAll(ITransportPeer peer)
    {
        foreach (CargoReactionBase reaction in sent.Keys.Where(r => r == null).ToList())
            sent.Remove(reaction);
        foreach (KeyValuePair<CargoReactionBase, FireState> kvp in sent)
            if (kvp.Value != FireState.None && TryLocate(kvp.Key, out NetworkedTrainCar networked, out byte index))
                NetworkLifecycle.Instance.Server.SendCargoFire(networked.NetId, index, kvp.Value, peer);
    }

    public static void Server_Ignite(TrainCar car, byte index, float strength, bool explode, Vector3 playerPosition, string username)
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

    // ---------------------------------------------------------------- client

    public static void Client_Receive(TrainCar car, byte index, FireState state)
    {
        CargoReactionBase[] reactions = ExplosionSync.CargoReactions(car);
        if (index >= reactions.Length)
            return;
        CargoReactionBase reaction = reactions[index];
        hostState[reaction] = state;
        if (state.HasFlag(FireState.Ignited) && !reaction.isIgnited)
            reaction.PlayIgnitionSound(reaction.transform.position);
        Apply(reaction);
    }

    // Holds the client's copy to the host's state, whatever its own simulation decided.
    internal static void Client_Hold(CargoReactionBase reaction)
    {
        if (IsClient && reaction.initialized)
            Apply(reaction);
    }

    private static void Apply(CargoReactionBase reaction)
    {
        hostState.TryGetValue(reaction, out FireState state);
        reaction.isIgnited = state.HasFlag(FireState.Ignited);
        reaction.aboutToExplode = state.HasFlag(FireState.AboutToExplode);
    }

    // A player's lighter or flare at a car: the host decides. False means not lit here (yet).
    internal static bool Client_ForwardIgnite(CargoReactionBase reaction, float strength, bool explode)
    {
        if (!LocalIgniter || !TryLocate(reaction, out NetworkedTrainCar networked, out byte index))
            return false;
        NetworkLifecycle.Instance.Client.SendCargoIgnite(networked.NetId, index, strength, explode);
        return false;
    }

    public static void Clear()
    {
        sent.Clear();
        hostState.Clear();
    }
}

[HarmonyPatch(typeof(CargoReactionBase), "Update")]
public static class CargoReactionUpdatePatch
{
    private static void Prefix(CargoReactionBase __instance) => CargoFireSync.Client_Hold(__instance);

    private static void Postfix(CargoReactionBase __instance)
    {
        CargoFireSync.Client_Hold(__instance);
        CargoFireSync.Host_AfterUpdate(__instance);
    }
}

[HarmonyPatch]
public static class CargoReactionIgnitePatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(CargoReactionBase), "ICargoReaction.TryIgniteExternally");

    private static bool Prefix(CargoReactionBase __instance, float ignitionStrength, ref bool __result)
    {
        if (!ExplosionSync.IsClient || ExplosionSync.ApplyingHostExplosion)
            return true;
        __result = CargoFireSync.Client_ForwardIgnite(__instance, ignitionStrength, explode: false);
        return false;
    }
}

[HarmonyPatch]
public static class CargoReactionExplodeExternallyPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(CargoReactionBase), "ICargoReaction.TryExplodeExternally");

    private static bool Prefix(CargoReactionBase __instance)
    {
        if (!ExplosionSync.IsClient || ExplosionSync.ApplyingHostExplosion)
            return true;
        CargoFireSync.Client_ForwardIgnite(__instance, 0f, explode: true);
        return false;
    }
}

// Only an item's igniter (a lighter, a flare) is a player lighting something.
[HarmonyPatch(typeof(Igniter), nameof(Igniter.IgniteNow))]
public static class IgniterPatch
{
    private static void Prefix() => CargoFireSync.LocalIgniter = true;

    private static void Finalizer() => CargoFireSync.LocalIgniter = false;
}
