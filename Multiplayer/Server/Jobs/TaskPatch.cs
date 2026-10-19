using DV.Logic.Job;
using HarmonyLib;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Session;
using System;

namespace Multiplayer.Server.Jobs;

[HarmonyPatch(typeof(Task))]
public static class TaskPatch
{
    [HarmonyPatch(nameof(Task.SetState))]
    [HarmonyPrefix]
    public static void SetStatePrefix(Task __instance, TaskState newState)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        if (newState == TaskState.InProgress)
            return;


        //Multiplayer.LogDebug(()=>$"Task.SetState() called for jobId: {__instance.Job.ID}, taskType: {__instance.InstanceTaskType}, newState: {newState}");
        if(!NetworkedTask.TryGet(__instance, out var networkedTask))
        {
            Multiplayer.LogError($"Task.SetState() could not find NetworkedTask for jobId: {__instance.Job.ID}, taskType: {__instance.InstanceTaskType}");
            return;
        }

        if (networkedTask.RecordState(newState))
            NetworkLifecycle.Instance.Server.SendTaskUpdate(networkedTask.NetId, newState,
                __instance.taskStartTime, __instance.taskFinishTime);
    }

    [HarmonyPatch(nameof(Task.SetJobBelonging))]
    [HarmonyPostfix]
    public static void SetJobBelongingPostfix(Task __instance, Job Job)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        NetworkedJob.EnqueueTask(__instance, Job);
    }
}
