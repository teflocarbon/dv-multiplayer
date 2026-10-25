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
/// The cassette is in at once all the same: the boombox's save in its first frame names its track
/// and whether it plays only with a cassette in (a stashed state's, OI-COLD-13), and an area set up
/// with one in leaves VR's insert trigger off, as vanilla's own insert does.
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
        if (container[sourceIndex] is { } loaded && loaded.GetComponent<Cassette>() is { } cassette)
        {
            try
            {
                __instance.InsertedCassette = cassette;
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
            }
        }
        return false;
    }

    private static IEnumerator AfterSetup(CassetteInteractionArea area, AItemContainer container)
    {
        float until = Time.realtimeSinceStartup + SetupWaitSeconds;
        while (area != null && area.initCoro != null && Time.realtimeSinceStartup < until)
            yield return null;
        waiting.Remove(area);
        if (area == null || area.areaControl == null || container == null)
            yield break;
        // The handler clicks the cassette in or out, as a 2D sound when the boombox isn't in the scene:
        // for the server's copy of a boombox someone else keeps (a player host's), that's a click its
        // own player hears from nowhere. A boombox in the scene clicks where it is, as vanilla's load does.
        Transform boombox = area.transform.parent;
        if (boombox != null ? boombox.gameObject.activeInHierarchy : area.gameObject.activeInHierarchy)
        {
            area.OnMagazineDataChanged(container, 0, -1);
            yield break;
        }
        AudioClip insert = area.insertCassetteSound, remove = area.removeCassetteSound;
        area.insertCassetteSound = area.removeCassetteSound = null;
        try
        {
            area.OnMagazineDataChanged(container, 0, -1);
        }
        finally
        {
            area.insertCassetteSound = insert;
            area.removeCassetteSound = remove;
        }
    }
}
