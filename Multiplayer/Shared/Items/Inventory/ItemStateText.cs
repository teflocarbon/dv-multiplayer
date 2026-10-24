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
    /// Loads a row's state into an object made this frame, for a take-out that can't wait for
    /// vanilla's frame (<c>StartingItemsController</c> loads a frame later), and finishes it as a
    /// container. Some items only set themselves up at the end of their first frame (a lantern's
    /// wick knob) and won't load active before then: those load inactive, as an item in a slot
    /// does, and show their state when next enabled (the lantern sets its knob then). One that
    /// won't load either way loads once its first frame is over, and the container is finished then.
    /// </summary>
    public static void LoadNowOrSoon(GameObject item, JObject state, string what)
    {
        ItemSaveData saveData = item != null ? item.GetComponent<ItemSaveData>() : null;
        if (saveData == null || state == null)
        {
            FinishContainer(item);
            return;
        }
        try
        {
            Load(saveData, state);
            FinishContainer(item);
            return;
        }
        catch (Exception) when (item.activeSelf)
        {
        }
        catch (Exception)
        {
            CoroutineManager.Instance.StartCoroutine(LoadLater(saveData, state, what));
            return;
        }
        item.SetActive(false);
        try
        {
            Load(saveData, state);
            item.SetActive(true);
            FinishContainer(item);
        }
        catch (Exception)
        {
            item.SetActive(true);
            CoroutineManager.Instance.StartCoroutine(LoadLater(saveData, state, what));
        }
    }

    private static void Load(ItemSaveData saveData, JObject state)
    {
        saveData.LoadItemData((JObject)state.DeepClone());
        saveData.PostLoadItemData();
    }

    public static void LoadNowOrSoon(GameObject item, string state, string what) =>
        LoadNowOrSoon(item, string.IsNullOrEmpty(state) ? null : JObject.Parse(state), what);

    private const int LoadAttempts = 5;

    private static System.Collections.IEnumerator LoadLater(ItemSaveData saveData, JObject state, string what)
    {
        yield return WaitFor.EndOfFrame;
        for (int attempt = 1; ; attempt++)
        {
            // Vanilla's own end-of-frame setup runs first: wait a frame past it.
            yield return null;
            if (saveData == null)
                yield break;
            try
            {
                Load(saveData, state);
                FinishContainer(saveData.gameObject);
                yield break;
            }
            catch (Exception exception)
            {
                if (attempt < LoadAttempts)
                    continue;
                Multiplayer.LogWarning($"Inventory: {what}'s saved state didn't load: {exception.Message}");
                FinishContainer(saveData.gameObject);
                yield break;
            }
        }
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
