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

    /// <summary>
    /// Makes an item's object from its prefab for its saved state, as vanilla's load does
    /// (<c>StartingItemsController</c>): a container (a toolbox, a magazine such as the boombox)
    /// doesn't register itself under a fresh id as it wakes, so its saved id registers it when its
    /// state loads. Without it the container registers twice and its state won't load. Call
    /// <see cref="FinishContainer"/> once its state is loaded, or isn't.
    /// </summary>
    public static GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        DV.InventorySystem.ItemContainerRegistry registry = DV.InventorySystem.Inventory.Instance?.ItemContainerRegistry;
        bool couldRegister = registry == null || registry.canRegisterContainers;
        if (registry != null)
            registry.canRegisterContainers = false;
        try
        {
            return UnityEngine.Object.Instantiate(prefab, position, rotation);
        }
        finally
        {
            if (registry != null)
                registry.canRegisterContainers = couldRegister;
        }
    }

    /// <summary>A container made by <see cref="Instantiate"/> that its state didn't register (none, or its id is taken) gets a fresh id, as vanilla's load does.</summary>
    public static void FinishContainer(GameObject item)
    {
        if (item != null && item.GetComponent<ItemSaveData>() is { } saveData)
            saveData.PostContainerLoadData();
    }
}
