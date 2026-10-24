using System;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.CabControls;
using DV.CabControls.Spec;
using DV.Items;
using DV.Common;
using DV.Localization;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// What an inventory slot holds under D14 (docs/design/items/inventory.md, "Stand-ins"): an
/// empty, inactive object with no renderer, collider, <c>ItemBase</c> or <c>NetworkedItem</c>,
/// carrying only its prefab's look (the <see cref="IInventoryItemSpec"/> vanilla's inventory, UI
/// and hotbar draw from) and its row: the NetId the server keeps the item by, its claim, its
/// relation and its own saved state. Nothing in it can fall, be swept, be saved by vanilla or be
/// destroyed as an item; the item's object is made from it when the item leaves the slot
/// (<see cref="ColdInventory"/>). It is the cold container UI's proxy made general.
/// </summary>
internal sealed class InventoryStandIn : MonoBehaviour, IInventoryItemSpec
{
    private static readonly Dictionary<ushort, InventoryStandIn> byNetId = new();
    private static Transform root;

    private InventoryItemSpec source;
    private Item sourceItem;

    public ushort NetId { get; private set; }
    public string PrefabName { get; private set; } = string.Empty;
    /// <summary>The item's own saved state (<c>ItemSaveData</c> JSON), or empty.</summary>
    public string State { get; set; } = string.Empty;
    public ItemRelation Relation { get; set; }
    public int ClaimSlot { get; set; } = -1;
    public ItemInventoryClaimFlags ClaimFlags { get; set; }
    public uint Revision { get; set; }
    /// <summary>Its prefab is known here: it can be drawn and taken out.</summary>
    public bool Known => source != null;

    public bool HasRetrievalClaim => ClaimSlot >= 0 &&
        (ClaimFlags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;

    public static IEnumerable<InventoryStandIn> All => byNetId.Values.Where(standIn => standIn != null);

    public static bool TryGet(ushort netId, out InventoryStandIn standIn) =>
        byNetId.TryGetValue(netId, out standIn) && standIn != null;

    public static bool Is(GameObject item) => item != null && item.GetComponent<InventoryStandIn>() != null;

    /// <summary>The stand-in for an item's row: the one it has, or a new one. Its prefab may be unknown.</summary>
    public static InventoryStandIn For(ushort netId, string prefabName)
    {
        if (TryGet(netId, out InventoryStandIn existing))
            return existing;
        if (root == null)
        {
            GameObject holder = new("[MP] Inventory stand-ins");
            holder.SetActive(false);
            Object.DontDestroyOnLoad(holder);
            root = holder.transform;
        }
        // Named as vanilla names a loaded item (its prefab), so GetItemByName still finds it.
        GameObject gameObject = new(prefabName ?? string.Empty);
        gameObject.transform.SetParent(root, false);
        gameObject.SetActive(false);
        InventoryStandIn standIn = gameObject.AddComponent<InventoryStandIn>();
        standIn.NetId = netId;
        standIn.PrefabName = prefabName ?? string.Empty;
        standIn.source = FindSpec(prefabName);
        standIn.sourceItem = standIn.source != null ? standIn.source.GetComponent<Item>() : null;
        byNetId[netId] = standIn;
        return standIn;
    }

    /// <summary>
    /// The prefab's spec from the game's item list, or null if it isn't one (a mod item without its
    /// mod). Job papers, which the job system makes from its resources, have theirs there.
    /// </summary>
    public static InventoryItemSpec FindSpec(string prefabName)
    {
        if (string.IsNullOrEmpty(prefabName))
            return null;
        InventoryItemSpec listed = Globals.G?.Items?.items?.FirstOrDefault(spec =>
            spec != null && string.Equals(spec.ItemPrefabName, prefabName, StringComparison.Ordinal));
        if (listed != null || !Jobs.JobPapers.IsPaper(prefabName))
            return listed;
        GameObject paper = Resources.Load<GameObject>(prefabName);
        return paper != null ? paper.GetComponent<InventoryItemSpec>() : null;
    }

    /// <summary>Whether an item of this prefab can be a stand-in: a prefab known here (job papers too, OI-COLD-07).</summary>
    public static bool CanStandIn(string prefabName) => !IsUnmadePaper(prefabName) && FindSpec(prefabName) != null;

    // An expired or missing-licence report is the job system's, but no row can make it again (it
    // has no recipe): it stays an object, as it's made generically from no job otherwise.
    private static bool IsUnmadePaper(string prefabName) =>
        prefabName is nameof(JobExpiredReport) or nameof(JobMissingLicenseReport);

    /// <summary>
    /// Whether this object can be a stand-in: its prefab can, and its magazine is empty or its ammo
    /// can be rows nested in its row (a boombox's cassette, a sprayer's can, a soldering tool's
    /// spool; <see cref="StandInAmmo"/>, OI-CON-16).
    /// </summary>
    public static bool CanStandIn(GameObject item) =>
        item != null && item.GetComponent<InventoryItemSpec>() is { } spec && CanStandIn(spec.ItemPrefabName) &&
        (!HoldsAmmo(item) || StandInAmmo.CanNest(item)) && (Jobs.JobPapers.PaperOf(item) == null || Jobs.JobPapers.CanBeRow(item));

    /// <summary>Whether a magazine of this object holds anything.</summary>
    public static bool HoldsAmmo(GameObject item) =>
        item.GetComponentsInChildren<ItemMagazine>(true)
            .Any(magazine => Enumerable.Range(0, magazine.Capacity).Any(slot => magazine[slot] != null));

    /// <summary>The stand-in an object of the local player's becomes as it's put away, with its row's fields and state.</summary>
    public static InventoryStandIn ForObject(NetworkedItem item)
    {
        InventoryStandIn standIn = For(item.NetId, item.Item.InventorySpecs.itemPrefabName);
        standIn.Relation = item.Relation;
        standIn.ClaimSlot = item.InventoryClaimSlot;
        standIn.ClaimFlags = item.InventoryClaimFlags;
        standIn.Revision = item.AuthorityRevision;
        standIn.State = StandInAmmo.Capture(item.gameObject);
        TutorialRadio.Keep(item);
        return standIn;
    }

    /// <summary>
    /// Into the slot its row has (OI-SLOT-01): an essential in its claim slot through the claim,
    /// anything else exactly, as an object was placed before D14; the first free slot if that fails,
    /// a refused claim too, reserved or not (OI-START-10).
    /// </summary>
    public void Place(ItemUpdate row)
    {
        bool inClaim = row.InventoryClaimSlot >= 0 &&
            (row.InventorySlot < 0 || row.InventorySlot == row.InventoryClaimSlot);
        bool placed = inClaim
            ? InventoryIntegration.RestoreClaim(gameObject, row.InventoryClaimSlot).Succeeded
            : row.InventorySlot >= 0 && InventoryIntegration.PlaceAt(gameObject, row.InventorySlot) >= 0;
        if (!placed && InventoryIntegration.AddActive(gameObject) < 0)
            Multiplayer.LogWarning($"Inventory: no slot for {PrefabName} (netId {NetId}), which the server has in slot {row.InventorySlot}");
    }

    /// <summary>Whether items of this prefab snap to the VR belt (an enabled belt slot keeps them as objects).</summary>
    public static bool IsBeltSnappablePrefab(string prefabName) =>
        BeltSnappable(FindSpec(prefabName)?.GetComponent<Item>());

    /// <summary>The prefab to make the item's object from, or null.</summary>
    public GameObject Prefab => source != null ? source.gameObject : null;

    public void Retire()
    {
        if (byNetId.TryGetValue(NetId, out InventoryStandIn kept) && kept == this)
            byNetId.Remove(NetId);
        Destroy(gameObject);
    }

    public static void RetireAll()
    {
        foreach (InventoryStandIn standIn in byNetId.Values.ToArray())
            if (standIn != null)
                Destroy(standIn.gameObject);
        byNetId.Clear();
    }

    // Vanilla's inventory hooks (InventoryDV) ask these of a slot's object. An essential reserves
    // its slot for its owner, never for someone else holding it.
    public bool IsEssentialForInventory => HasRetrievalClaim ||
        source != null && source.IsEssential && Relation != ItemRelation.SomeoneElses;
    public bool IsBeltSnappable => BeltSnappable(sourceItem);
    public bool IsTwoHandedItem => VRManager.IsVREnabled() && sourceItem != null &&
        sourceItem.controllerAttachMethod == ItemControllerAttachMethod.TwoHandedPole;

    // As ItemBase sets itself up from its spec: the belt is there only in VR.
    private static bool BeltSnappable(Item spec) => spec != null && VRManager.IsVREnabled() &&
        (spec.allowedSnapPointTypes & SnapPointTypes.Belt) != 0;

    // The row's name: a job's id on its papers, a label, as the item's own localizer gives it.
    private string NameParam()
    {
        if (string.IsNullOrEmpty(State))
            return null;
        try
        {
            JObject state = JObject.Parse(State);
            return Jobs.JobPapers.NameOf(state) ?? (string)state["feeId"];
        }
        catch
        {
            return null;
        }
    }

    private string Label()
    {
        if (string.IsNullOrEmpty(State))
            return null;
        try
        {
            return (string)JObject.Parse(State)["label_text"];
        }
        catch
        {
            return null;
        }
    }

    public GameObject GetGameObject() => gameObject;
    public bool BelongsToPlayer { get => source != null && source.BelongsToPlayer || Relation == ItemRelation.Mine; set { } }
    public bool ImmuneToDumpster { get => source?.ImmuneToDumpster ?? false; set { } }
    public bool IsEssential { get => source?.IsEssential ?? false; set { } }
    public string ItemPrefabName => source?.ItemPrefabName ?? PrefabName;
    public string LocalizationKey => source?.LocalizationKey ?? string.Empty;
    public string LocalizedName
    {
        get
        {
            string label = Label();
            if (!string.IsNullOrEmpty(label))
                return label;
            if (source == null)
                return PrefabName;
            string param = NameParam();
            return param == null ? source.LocalizedName : LocalizationAPI.L(source.LocalizationKey, param);
        }
    }
    public string LocalizedDescription => source?.LocalizedDescription ?? string.Empty;
    public GameObject PreviewPrefab { get => source?.PreviewPrefab; set { } }
    public Bounds PreviewBounds { get => source?.PreviewBounds ?? default; set { } }
    public Vector3 PreviewRotation => source?.PreviewRotation ?? Vector3.zero;
    public Sprite ItemIconSpriteSimple => source?.ItemIconSpriteSimple;
    public Sprite ItemIconSprite => source?.ItemIconSprite;
    public Sprite ItemIconSpriteDropped => source?.ItemIconSpriteDropped;
}
