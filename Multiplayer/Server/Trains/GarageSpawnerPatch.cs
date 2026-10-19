using HarmonyLib;
using Multiplayer.Shared.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Multiplayer.Server.Trains;

[HarmonyPatch(typeof(GarageCarSpawner))]
public static class GarageSpawnerPatch
{
    [HarmonyPatch(nameof(GarageCarSpawner.AllowSpawning))]
    [HarmonyPrefix]
    private static bool AllowSpawning(GarageCarSpawner __instance)
    {
        //we don't want the client to also spawn
        return NetworkLifecycle.Instance.IsHost();
    }
}
