using System;
using System.Reflection;
using HarmonyLib;
using LocoSim.Implementations;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Trains;

public static class SimComponent_Tick_Patch
{
    /// <summary>Set while a client works out a component's outputs itself (TrainPortPresenter's
    /// prediction), the one time a client's simulation component ticks.</summary>
    [ThreadStatic]
    internal static bool LocalTick;

    public static void Patch(Harmony harmony)
    {
        HarmonyMethod prefixMethod = new(AccessTools.DeclaredMethod(typeof(SimComponent_Tick_Patch), nameof(Prefix)));
        foreach (Type type in Assembly.GetAssembly(typeof(SimComponent)).GetTypes())
        {
            if (!type.IsSubclassOf(typeof(SimComponent)))
                continue;
            MethodInfo tickMethod = type.GetMethod(nameof(SimComponent.Tick), AccessTools.all);
            harmony.Patch(tickMethod, prefixMethod);
        }
    }

    private static bool Prefix()
    {
        return LocalTick || !NetworkRole.IsRemoteClient;
    }
}
