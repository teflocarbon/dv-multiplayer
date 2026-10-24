using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// An item's own saved state (its <c>ItemSaveData</c> JObject) as the text a row carries on the
/// wire (D14, OI-COLD-13): a lighter's fuel, duct tape's uses, a spool's ammo, a label.
/// </summary>
internal static class ItemStateText
{
    /// <summary>The object's state now, or empty when it has none or it won't save.</summary>
    public static string Capture(GameObject item)
    {
        if (item == null || item.GetComponent<ItemSaveData>() is not { } saveData)
            return string.Empty;
        try
        {
            return saveData.SaveItemData()?.ToString(Formatting.None) ?? string.Empty;
        }
        catch (Exception exception)
        {
            Multiplayer.LogWarning($"Item state of {item.name} won't save: {exception.Message}");
            return string.Empty;
        }
    }

    /// <summary>Loads a row's state into a freshly made object, as vanilla's load does.</summary>
    public static void Load(GameObject item, string state)
    {
        if (string.IsNullOrEmpty(state) || item == null || item.GetComponent<ItemSaveData>() is not { } saveData)
            return;
        saveData.LoadItemData(JObject.Parse(state));
        saveData.PostLoadItemData();
    }
}
