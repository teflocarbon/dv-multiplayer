using System;
using System.Runtime.CompilerServices;
using DV.Logic.Job;
using HarmonyLib;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.Server.Jobs;

/// <summary>
/// Loading a save, DV matches each taken job to its saved booklet, and when there is none it prints
/// a replacement straight into the local player's inventory (JobSaveManager.InitializeCorrespondingJobBooklet,
/// CreateAndInitializeJobBookletForJob). The mod saves no booklets (WorldItemPersistenceSave.IsJobSystemDocument),
/// so on every load each taken job gets one. A dedicated host's local player is a parked body, not
/// a player (as with StartingItemsControllerPatch): the server can't possess items, so the booklet
/// sat in an inventory nobody has while its record said World, and clients saw it lying at the
/// world origin. A dedicated host prints none; players reprint their booklet at a validator
/// (JobBookletSummonCoordinator), as a player who took the job on a player host does after a load.
/// <para>
/// A player host's booklet waits until the host's own items are back in their slots (PlayerItems.Join,
/// after the ledger's load): vanilla's inventory starts empty in multiplayer (docs/design/items/save-load.md),
/// so a booklet put in first took the first slot vanilla had free, one a saved item of the host's
/// still held, and the ledger refused it while vanilla kept it.
/// </para>
/// </summary>
[HarmonyPatch]
public static class JobBookletRecoveryPatch
{
    // How long a booklet waits for the host's items before it goes in anyway.
    private const float MaxWait = 20f;

    private static readonly ConditionalWeakTable<object, StrongBox<float>> waitingSince = new();

    // The coroutine's own MoveNext: the iterator method is a stub Mono inlines into its caller.
    private static MethodBase TargetMethod() =>
        AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(JobSaveManager), "CreateAndInitializeJobBookletForJob"));

    [HarmonyPrefix]
    private static bool SkipOnDedicatedHost(object __instance, ref bool __result)
    {
        if (!NetworkLifecycle.Instance.IsDedicatedHost)
            return !WaitForHostItems(__instance, ref __result);
        string jobId = Traverse.Create(__instance).Field<Job>("job").Value?.ID;
        Multiplayer.Log($"Job {jobId}: no booklet printed into the dedicated host's inventory on load; players reprint it at a validator.");
        __result = false;
        return false;
    }

    // True (and the coroutine yields another frame) while the booklet is made but the host's own
    // saved items aren't in their slots yet. Its first step, making the booklet, runs as vanilla's.
    private static bool WaitForHostItems(object coroutine, ref bool result)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true || Traverse.Create(coroutine).Field<int>("<>1__state").Value == 0)
            return false;
        StrongBox<float> since = waitingSince.GetValue(coroutine, _ => new StrongBox<float>(Time.realtimeSinceStartup));
        Guid host = NetworkServer.Instance?.LocalPlayer?.Guid ?? Guid.Empty;
        bool waiting = !ServerLedger.Loaded || host == Guid.Empty || ColdInventories.Of(host).Count > 0;
        if (!waiting || float.IsNaN(since.Value))
            return false;
        if (Time.realtimeSinceStartup - since.Value > MaxWait)
        {
            Multiplayer.LogWarning($"Job {Traverse.Create(coroutine).Field<Job>("job").Value?.ID}: its booklet waited {MaxWait:0} s " +
                                   "for the host's items to go back in their slots, and goes in now");
            since.Value = float.NaN;
            return false;
        }
        result = true;
        return true;
    }
}
