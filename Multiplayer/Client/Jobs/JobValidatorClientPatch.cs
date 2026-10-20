using System.Collections;
using HarmonyLib;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Session;
using UnityEngine;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Jobs;

/// <summary>
/// A remote client asks the server to take or hand in a job instead of running the validator; the
/// server's half is Server/Jobs/JobValidatorServerPatch. The reprint button is a request from every
/// player, the host's own included (handled in-process).
/// </summary>
[HarmonyPatch(typeof(JobValidator))]
public static class JobValidator_ClientPatch
{
    private const float TIME_OUT = 3f;

    [HarmonyPatch(nameof(JobValidator.ProcessJobOverview))]
    [HarmonyPrefix]
    private static bool ProcessJobOverview(JobValidator __instance, JobOverview jobOverview)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return true;
        if (!JobValidator_Patch.TryGetOverviewJob(__instance, jobOverview, out NetworkedJob networkedJob))
            return false;

        if (!networkedJob.ValidatorRequestSent)
            SendValidationRequest(__instance, networkedJob, ValidationType.JobOverview,
                jobOverview.GetComponent<NetworkedItem>());

        return false;
    }

    [HarmonyPatch(nameof(JobValidator.SummonAllActiveJobBooklets))]
    [HarmonyPrefix]
    private static bool SummonAllActiveJobBooklets(JobValidator __instance)
    {
        // No player session here: vanilla.
        IJobsClient jobs = JobsClient.Current;
        if (jobs == null)
            return true;
        if (!NetworkedStationController.GetFromJobValidator(__instance,
                out NetworkedStationController station))
        {
            __instance.bookletPrinter.PlayErrorSound();
            return false;
        }
        jobs.SendJobBookletSummonRequest(station.NetId);
        return false;
    }

    [HarmonyPatch(nameof(JobValidator.ValidateJob))]
    [HarmonyPrefix]
    private static bool ValidateJob_Prefix(JobValidator __instance, JobBooklet jobBooklet)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return true;
        if (!JobValidator_Patch.TryGetBookletJob(__instance, jobBooklet, out NetworkedJob networkedJob))
            return false;

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

            NetworkClient.Instance.SendJobValidateRequest(netJob, networkedStation,
                validationItem);
            CoroutineManager.Instance.StartCoroutine(AwaitResponse(validator, netJob));
        }
        else
        {
            NetworkClient.Instance.LogError($"Failed to validate {type} for {netJob?.Job?.ID}. NetworkedStation not found!");
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

        //WaitForSecondsRealtime(Math.Max(4f,(NetworkClient.Instance.Ping * 4f)/1000));

        bool received = networkedJob.ValidatorResponseReceived;
        bool accepted = networkedJob.ValidationAccepted;

        var receivedStr = received ? "received" : "timed out";
        var acceptedStr = accepted ? " Accepted" : " Rejected";

        NetworkClient.Instance.Log($"Job Validation Response {receivedStr} for {networkedJob?.Job?.ID}.{acceptedStr}");

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
