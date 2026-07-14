using DV.Booklets;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Components.Networking;
using UnityEngine;

namespace Multiplayer.Patches.SaveGame;

/// <summary>Licenses are synchronized entitlements; multiplayer never creates physical papers.</summary>
[HarmonyPatch]
internal static class PhysicalGeneralLicensePaperPatch
{
    private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(BookletCreator),
        nameof(BookletCreator.CreateLicense), new[] { typeof(GeneralLicenseType_v2), typeof(Vector3),
            typeof(Quaternion), typeof(Transform) });

    [HarmonyPrefix]
    private static bool Prefix(ref GameObject __result)
    {
        if (NetworkLifecycle.Instance == null ||
            !NetworkLifecycle.Instance.IsServerRunning && !NetworkLifecycle.Instance.IsClientRunning)
            return true;
        __result = null;
        return false;
    }
}

[HarmonyPatch]
internal static class PhysicalJobLicensePaperPatch
{
    private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(BookletCreator),
        nameof(BookletCreator.CreateLicense), new[] { typeof(JobLicenseType_v2), typeof(Vector3),
            typeof(Quaternion), typeof(Transform) });

    [HarmonyPrefix]
    private static bool Prefix(ref GameObject __result)
    {
        if (NetworkLifecycle.Instance == null ||
            !NetworkLifecycle.Instance.IsServerRunning && !NetworkLifecycle.Instance.IsClientRunning)
            return true;
        __result = null;
        return false;
    }
}

[HarmonyPatch]
internal static class PhysicalGeneralLicensePaperDirectPatch
{
    private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(BookletCreator_Licenses),
        nameof(BookletCreator_Licenses.CreateLicense), new[] { typeof(GeneralLicenseType_v2), typeof(Vector3),
            typeof(Quaternion), typeof(Transform), typeof(bool) });

    [HarmonyPrefix]
    private static bool Prefix(ref GameObject __result)
    {
        if (NetworkLifecycle.Instance == null ||
            !NetworkLifecycle.Instance.IsServerRunning && !NetworkLifecycle.Instance.IsClientRunning)
            return true;
        __result = null;
        return false;
    }
}

[HarmonyPatch]
internal static class PhysicalJobLicensePaperDirectPatch
{
    private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(BookletCreator_Licenses),
        nameof(BookletCreator_Licenses.CreateLicense), new[] { typeof(JobLicenseType_v2), typeof(Vector3),
            typeof(Quaternion), typeof(Transform), typeof(bool) });

    [HarmonyPrefix]
    private static bool Prefix(ref GameObject __result)
    {
        if (NetworkLifecycle.Instance == null ||
            !NetworkLifecycle.Instance.IsServerRunning && !NetworkLifecycle.Instance.IsClientRunning)
            return true;
        __result = null;
        return false;
    }
}
