using System.Collections;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Jobs;
using UnityEngine;

namespace Multiplayer.Patches.Jobs;

[HarmonyPatch(typeof(JobValidator))]
public static class JobValidator_Patch
{
    private const float TIME_OUT = 3f;

    [HarmonyPatch(nameof(JobValidator.Start))]
    [HarmonyPostfix]
    private static void Start(JobValidator __instance)
    {
        //Multiplayer.Log($"JobValidator Awake!");
        NetworkedStationController.QueueJobValidator(__instance);
    }


    [HarmonyPatch(nameof(JobValidator.ProcessJobOverview))]
    [HarmonyPrefix]
    private static bool ProcessJobOverview(JobValidator __instance, JobOverview jobOverview)
    {

        if(__instance.bookletPrinter.IsOnCooldown)
        {
            __instance.bookletPrinter.PlayErrorSound();
            return false;
        }

        if(!NetworkedJob.TryGetFromJob(jobOverview.job, out NetworkedJob networkedJob) || jobOverview.job.State != JobState.Available)
        {
            NetworkLifecycle.Instance.Client.LogWarning($"Processing JobOverview {jobOverview?.job?.ID} {(networkedJob == null ? "NetworkedJob not found!, " : "")}Job state: {jobOverview?.job?.State}");
            __instance.bookletPrinter.PlayErrorSound();
            jobOverview.DestroyJobOverview();
            return false;
        }

        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.Server.Log($"Processing JobOverview {jobOverview?.job?.ID}");
            networkedJob.JobValidator = __instance;
            if (networkedJob.PendingBookletIssuedToPlayerId == 0)
                networkedJob.PendingBookletIssuedToPlayerId =
                    NetworkLifecycle.Instance.Server.SelfId;
            return true;
        }

        if (!networkedJob.ValidatorRequestSent)
            SendValidationRequest(__instance, networkedJob, ValidationType.JobOverview,
                jobOverview.GetComponent<NetworkedItem>());

        return false;
    }

    [HarmonyPatch(nameof(JobValidator.SummonAllActiveJobBooklets))]
    [HarmonyPrefix]
    private static bool SummonAllActiveJobBooklets(JobValidator __instance)
    {
        if (NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsClientRunning)
            return true;
        if (!NetworkedStationController.GetFromJobValidator(__instance,
                out NetworkedStationController station))
        {
            __instance.bookletPrinter.PlayErrorSound();
            return false;
        }
        if (NetworkLifecycle.Instance.IsHost())
        {
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(
                    NetworkLifecycle.Instance.Server.SelfId, out ServerPlayer host))
                JobBookletSummonCoordinator.TryStart(__instance, host);
        }
        else
        {
            NetworkLifecycle.Instance.Client.SendJobBookletSummonRequest(station.NetId);
        }
        return false;
    }

    [HarmonyPatch(nameof(JobValidator.ProcessJobOverview))]
    [HarmonyPostfix]
    private static void ProcessJobOverviewComplete(JobOverview jobOverview)
    {
        if (NetworkLifecycle.Instance.IsHost() && jobOverview?.job != null &&
            NetworkedJob.TryGetFromJob(jobOverview.job, out NetworkedJob networkedJob))
            networkedJob.PendingBookletIssuedToPlayerId = 0;
    }


    [HarmonyPatch(nameof(JobValidator.ValidateJob))]
    [HarmonyPrefix]
    private static bool ValidateJob_Prefix(JobValidator __instance, JobBooklet jobBooklet)
    {
        if (__instance.bookletPrinter.IsOnCooldown)
        {
            __instance.bookletPrinter.PlayErrorSound();
            return false;
        }

        if (!NetworkedJob.TryGetFromJob(jobBooklet.job, out NetworkedJob networkedJob) || jobBooklet.job.State != JobState.InProgress)
        {
            NetworkLifecycle.Instance.Client.LogWarning($"Validating Job {jobBooklet?.job?.ID} {(networkedJob == null ? "NetworkedJob not found!, " : "")}Job state: {jobBooklet?.job?.State}");
            __instance.bookletPrinter.PlayErrorSound();
            jobBooklet.DestroyJobBooklet();
            return false;
        }

        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.Server.Log($"Validating Job {jobBooklet?.job?.ID}");
            networkedJob.JobValidator = __instance;
            return true;
        }

        if (!networkedJob.ValidatorRequestSent)
            SendValidationRequest(__instance, networkedJob, ValidationType.JobBooklet,
                jobBooklet.GetComponent<NetworkedItem>());

        return false;
    }

    private static void SendValidationRequest(JobValidator validator, NetworkedJob netJob,
        ValidationType type, NetworkedItem validationItem)
    {
        //find the current station we're at
        if (NetworkedStationController.GetFromJobValidator(validator, out NetworkedStationController networkedStation))
        {
            //Set initial job state parameters
            netJob.ValidatorRequestSent = true;
            netJob.ValidatorResponseReceived = false;
            netJob.ValidationAccepted = false;
            netJob.JobValidator = validator;
            netJob.ValidationType = type;

            NetworkLifecycle.Instance.Client.SendJobValidateRequest(netJob, networkedStation,
                validationItem);
            CoroutineManager.Instance.StartCoroutine(AwaitResponse(validator, netJob));
        }
        else
        {
            NetworkLifecycle.Instance.Client.LogError($"Failed to validate {type} for {netJob?.Job?.ID}. NetworkedStation not found!");
            validator.bookletPrinter.PlayErrorSound();
        }
    }
    private static IEnumerator AwaitResponse(JobValidator validator, NetworkedJob networkedJob)
    {
        Multiplayer.LogDebug(() => $"Awaiting validation response for {networkedJob?.Job?.ID}...");

        float timeout = Time.time;

        //Book spawns can take a few seconds, this may be due to how the asset is loaded and rendered
        yield return new WaitUntil
        (
            () =>
            {
                return networkedJob.ValidatorResponseReceived || (Time.time - timeout > TIME_OUT);
            }
        );

        //WaitForSecondsRealtime(Math.Max(4f,(NetworkLifecycle.Instance.Client.Ping * 4f)/1000));

        bool received = networkedJob.ValidatorResponseReceived;
        bool accepted = networkedJob.ValidationAccepted;

        var receivedStr = received ? "received" : "timed out";
        var acceptedStr = accepted ? " Accepted" : " Rejected";

        NetworkLifecycle.Instance.Client.Log($"Job Validation Response {receivedStr} for {networkedJob?.Job?.ID}.{acceptedStr}");

        if (networkedJob == null)
        {
            validator.bookletPrinter.PlayErrorSound();
            yield break;
        }

        if(!received || !accepted)
        {
            validator.bookletPrinter.PlayErrorSound();
        }

        networkedJob.ValidatorRequestSent = false;
        networkedJob.ValidatorResponseReceived = false;
        networkedJob.ValidationAccepted = false;

    }
}
