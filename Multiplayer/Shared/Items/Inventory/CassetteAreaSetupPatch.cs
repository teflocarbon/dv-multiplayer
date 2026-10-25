using System.Collections;
using System.Collections.Generic;
using DV.InventorySystem;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// A cassette loaded into a boombox in the boombox's first frame (a take-out makes the boombox from
/// its row and its cassette with it, <see cref="StandInAmmo"/>; the server makes its own copy the
/// same way). Vanilla's cassette area sets itself up a frame after it wakes (its Init), and its
/// magazine handler needs that: it threw, the load with it, and the cassette was left loose and
/// inactive. Vanilla's load waits a frame, so it never sees this. The handler runs once the area is
/// set up instead; it reads what the magazine holds then, as vanilla's load would have found it.
/// </summary>
[HarmonyPatch(typeof(CassetteInteractionArea), nameof(CassetteInteractionArea.OnMagazineDataChanged))]
internal static class CassetteAreaSetupPatch
{
    // How long an area is waited for: one whose Init threw (its boombox gone meanwhile) never is.
    private const float SetupWaitSeconds = 5f;

    private static readonly HashSet<CassetteInteractionArea> waiting = new();

    private static bool Prefix(CassetteInteractionArea __instance, AItemContainer container, int sourceIndex, int destinationIndex)
    {
        if (__instance.areaControl != null || __instance.ignoreMagazineDataChanged || sourceIndex != 0 || destinationIndex != -1)
            return true;
        if (waiting.Add(__instance))
            CoroutineManager.Instance.StartCoroutine(AfterSetup(__instance, container));
        return false;
    }

    private static IEnumerator AfterSetup(CassetteInteractionArea area, AItemContainer container)
    {
        float until = Time.realtimeSinceStartup + SetupWaitSeconds;
        while (area != null && area.initCoro != null && Time.realtimeSinceStartup < until)
            yield return null;
        waiting.Remove(area);
        if (area != null && area.areaControl != null && container != null)
            area.OnMagazineDataChanged(container, 0, -1);
    }
}
