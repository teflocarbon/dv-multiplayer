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
/// CreateAndInitializeJobBookletForJob). The mod doesn't save booklets as vanilla's items: a booklet a
/// player carries is saved with them as a row, and is back in their slot when they're back
/// (JobPaperRows; D14, OI-COLD-15), so a job whose booklet a player has gets no reprint. The others
/// are booklets that lay about, which the mod doesn't save.
/// <para>
/// A dedicated host's local player is a parked body, not a player (as with StartingItemsControllerPatch):
/// the server can't possess items, so the booklet sat in an inventory nobody has while its record said
/// World, and clients saw it lying at the world origin. A dedicated host prints none; players reprint
/// their booklet at a validator (JobBookletSummonCoordinator), as a player who took the job on a player
/// host does after a load.
/// </para>
/// <para>
/// A player host's reprint waits until the ledger is loaded (it says which booklets players carry)
/// and the host's own items are back in their slots (PlayerItems.Join): vanilla's inventory starts
/// empty in multiplayer (docs/design/items/save-load.md), so a booklet put in first took the first
/// slot vanilla had free, one a saved item of the host's still held, and the ledger refused it while
/// vanilla kept it.
/// </para>
/// </summary>
[HarmonyPatch]
public static class JobBookletRecoveryPatch
{
    // How long a reprint waits for the host's items before it goes in anyway.
    private const float MaxWait = 20f;

    private static readonly ConditionalWeakTable<object, StrongBox<float>> waitingSince = new();

    // The coroutine's own MoveNext: the iterator method is a stub Mono inlines into its caller.
    private static MethodBase TargetMethod() =>
        AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(JobSaveManager), "CreateAndInitializeJobBookletForJob"));

    [HarmonyPrefix]
    private static bool SkipOnDedicatedHost(object __instance, ref bool __result)
    {
        Job job = Traverse.Create(__instance).Field<Job>("job").Value;
        if (!NetworkLifecycle.Instance.IsDedicatedHost)
            return !WaitOrSkip(__instance, job, ref __result);
        Multiplayer.Log($"Job {job?.ID}: no booklet printed into the dedicated host's inventory on load; " +
                        "a player who carried it has it back, others reprint it at a validator.");
        __result = false;
        return false;
    }

    // True (the coroutine yields another frame, or ends) while the ledger isn't loaded or the host's
    // own saved items aren't in their slots yet, and when a player carries the job's booklet. Its
    // first step, making the booklet, waits for both.
    private static bool WaitOrSkip(object coroutine, Job job, ref bool result)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true || Traverse.Create(coroutine).Field<int>("<>1__state").Value != 0)
            return false;
        StrongBox<float> since = waitingSince.GetValue(coroutine, _ => new StrongBox<float>(Time.realtimeSinceStartup));
        if (float.IsNaN(since.Value))
            return false;
        Guid host = NetworkServer.Instance?.LocalPlayer?.Guid ?? Guid.Empty;
        bool waiting = !ServerLedger.Loaded || host == Guid.Empty || PlayerItems.RowsPending(host);
        if (waiting && Time.realtimeSinceStartup - since.Value <= MaxWait)
        {
            result = true;
            return true;
        }
        if (JobPaperRows.HasBookletRow(job?.ID))
        {
            Multiplayer.Log($"Job {job?.ID}: no booklet reprinted on load; a player carries it");
            result = false;
            return true;
        }
        if (waiting)
            Multiplayer.LogWarning($"Job {job?.ID}: its booklet waited {MaxWait:0} s for the host's items to go back in their slots, and goes in now");
        since.Value = float.NaN;
        return false;
    }
}
