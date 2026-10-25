using CommandTerminal;
using DV.CabControls;
using HarmonyLib;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Server.Items.Behaviours;

/// <summary>
/// The dev console's item destroys on the server (OI-VAN-04; docs/design/items/vanilla-storage.md,
/// "The dev console"): each item they destroy ends with the cause <c>DevConsole</c>, an owned one's
/// entry with it, not as "destroyed by the game". Its spawns run as vanilla's, here on the server,
/// so they are unowned. A client's are refused (<c>Client/Items/Behaviours/DevConsoleRefusalPatch</c>).
/// </summary>
[HarmonyPatch(typeof(DV.Console))]
internal static class DevConsolePatch
{
    private static bool Serving => NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsSessionRunning &&
                                   NetworkLifecycle.Instance.IsHost();

    [HarmonyPatch("Debug_DestroyAllItems")]
    [HarmonyPrefix]
    private static void DestroyAllItems()
    {
        if (!Serving || Terminal.IssuedError)
            return;
        foreach (ItemBase item in Object.FindObjectsOfType<ItemBase>())
            Note(item.gameObject, "Debug.DestroyAllItems");
    }

    [HarmonyPatch("Debug_DestroyBuggedObjects")]
    [HarmonyPrefix]
    private static void DestroyBuggedObjects()
    {
        if (!Serving || Terminal.IssuedError)
            return;
        foreach (NetworkedItem item in NetworkedItem.GetAll())
        {
            Vector3 at = item != null ? item.transform.position : Vector3.zero;
            if (float.IsNaN(at.x) || float.IsNaN(at.y) || float.IsNaN(at.z))
                Note(item.gameObject, "Debug.DestroyBuggedObjects, its position NaN");
        }
    }

    // Vanilla destroys it next, this frame.
    private static void Note(GameObject obj, string command)
    {
        if (obj.TryGetComponent(out NetworkedItem item) && item.NetId != 0)
            item.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.DevConsole, NetworkServer.Instance?.LocalPlayer, command));
    }
}
