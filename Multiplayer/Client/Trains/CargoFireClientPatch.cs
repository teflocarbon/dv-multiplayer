using System.Collections.Generic;
using DV.Hazmat;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Trains;

/// <summary>
/// A client holds its cars' cargo fires to the host's, and asks the host to light one when its
/// player does (see <see cref="CargoFireSync"/>).
/// </summary>
public static class CargoFirePlayback
{
    // The host's state for each reaction.
    private static readonly Dictionary<CargoReactionBase, CargoFireSync.FireState> hostState = new();

    // Set while an item's igniter (a lighter or flare) runs: only those ignitions come from a player.
    // The rest on a client are its replay of what the host does anyway (an explosion igniting the
    // cars around it).
    internal static bool LocalIgniter;

    public static void Receive(TrainCar car, byte index, CargoFireSync.FireState state)
    {
        CargoReactionBase[] reactions = ExplosionSync.CargoReactions(car);
        if (index >= reactions.Length)
            return;
        CargoReactionBase reaction = reactions[index];
        hostState[reaction] = state;
        if (state.HasFlag(CargoFireSync.FireState.Ignited) && !reaction.isIgnited)
            reaction.PlayIgnitionSound(reaction.transform.position);
        Apply(reaction);
    }

    // Holds the client's copy to the host's state, whatever its own simulation decided.
    internal static void Hold(CargoReactionBase reaction)
    {
        if (NetworkRole.IsRemoteClient && reaction.initialized)
            Apply(reaction);
    }

    private static void Apply(CargoReactionBase reaction)
    {
        hostState.TryGetValue(reaction, out CargoFireSync.FireState state);
        reaction.isIgnited = state.HasFlag(CargoFireSync.FireState.Ignited);
        reaction.aboutToExplode = state.HasFlag(CargoFireSync.FireState.AboutToExplode);
    }

    // A player's lighter or flare at a car: the host decides. False means not lit here (yet).
    internal static bool ForwardIgnite(CargoReactionBase reaction, float strength, bool explode)
    {
        if (!LocalIgniter || !CargoFireSync.TryLocate(reaction, out NetworkedTrainCar networked, out byte index))
            return false;
        NetworkClient.Instance.SendCargoIgnite(networked.NetId, index, strength, explode);
        return false;
    }
}

[HarmonyPatch(typeof(CargoReactionBase), "Update")]
public static class CargoReactionUpdatePatch
{
    private static void Prefix(CargoReactionBase __instance) => CargoFirePlayback.Hold(__instance);

    private static void Postfix(CargoReactionBase __instance) => CargoFirePlayback.Hold(__instance);
}

[HarmonyPatch]
public static class CargoReactionIgnitePatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(CargoReactionBase), "ICargoReaction.TryIgniteExternally");

    private static bool Prefix(CargoReactionBase __instance, float ignitionStrength, ref bool __result)
    {
        if (!NetworkRole.IsRemoteClient || ExplosionPlayback.ApplyingHostExplosion)
            return true;
        __result = CargoFirePlayback.ForwardIgnite(__instance, ignitionStrength, explode: false);
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
        if (!NetworkRole.IsRemoteClient || ExplosionPlayback.ApplyingHostExplosion)
            return true;
        CargoFirePlayback.ForwardIgnite(__instance, 0f, explode: true);
        return false;
    }
}

// Only an item's igniter (a lighter, a flare) is a player lighting something.
[HarmonyPatch(typeof(Igniter), nameof(Igniter.IgniteNow))]
public static class IgniterPatch
{
    private static void Prefix() => CargoFirePlayback.LocalIgniter = true;

    private static void Finalizer() => CargoFirePlayback.LocalIgniter = false;
}
