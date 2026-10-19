using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Collections;
using Unity.Mathematics;

namespace Multiplayer.Patches.Train;

/// <summary>
/// DV picks each car's physics LOD (interior, walkable and item colliders, items riding on it) from
/// its distance to the nearest of a list of reference points: the local player, plus the external
/// camera when it's on. A server simulates cars for every player, so that isn't enough there: on a
/// dedicated host the local player is a parked body, and on a player host a car next to a far player
/// sat at the far LODs. This adds every player's position to that list.
///
/// The list is filled and handed to a scheduled job inside OnUpdate, so it can't be changed in a
/// postfix: the transpiler swaps the load of the list for the hand-over with a call that returns
/// the same list with the players added. Only a multiplayer server adds anything (on a player host
/// that includes its own player again, which changes nothing).
/// </summary>
[HarmonyPatch(typeof(DistanceLodSystem), "OnUpdate")]
public static class DistanceLodSystemPatch
{
    private static readonly FieldInfo referenceWorldPositions =
        AccessTools.Field(typeof(DistanceLodSystem), nameof(DistanceLodSystem.referenceWorldPositions));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new(instructions);
        bool patched = false;
        for (int i = 0; i + 1 < code.Count; i++)
        {
            // ldarg.0; ldfld referenceWorldPositions; stfld <display class>::worldPositions
            if (code[i].LoadsField(referenceWorldPositions) && code[i + 1].opcode == OpCodes.Stfld &&
                code[i + 1].operand is FieldInfo target && target.Name == "worldPositions")
            {
                code[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DistanceLodSystemPatch), nameof(WithPlayers)))
                    .MoveLabelsFrom(code[i]);
                patched = true;
                break;
            }
        }
        if (!patched)
            Multiplayer.LogError("DistanceLodSystemPatch: the reference-position hand-over wasn't found; other players won't count for physics LOD on the server");
        return code;
    }

    private static NativeList<float3> WithPlayers(DistanceLodSystem system)
    {
        // NativeList is a handle: adding through this copy adds to the system's own list.
        NativeList<float3> positions = system.referenceWorldPositions;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle != null && lifecycle.IsHost() && !lifecycle.Server.IsSinglePlayer)
        {
            foreach (ServerPlayer player in lifecycle.Server.GameplayPlayers)
                if (player.TrackingData.Position.HasValue)
                    positions.Add(player.WorldPosition);
        }
        return positions;
    }
}
