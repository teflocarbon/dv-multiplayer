using System.Collections.Generic;
using System.Linq;
using DV.CabControls;
using DV.Shops;
using HarmonyLib;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Shops;
using UnityEngine;

namespace Multiplayer.Client.Shops;

/// <summary>
/// A shop scanner as every game sees it (docs/design/shops.md, "The scanner"). Vanilla's
/// <c>ShopScanner</c> only works for its own holder: the laser, the screen (the aimed item's name,
/// units in the basket against stock, price) and the hover sound. The holder's game sends what the
/// scanner points at whenever it changes; the others show the same screen, play the hover sound at
/// the scanner, and show the laser while another player holds it. The description notification
/// stays on the holder's HUD. Scan and error sounds come with the register's state
/// (<see cref="ShopRegisterView"/>).
/// </summary>
public static class ShopScannerView
{
    public const byte NoModule = byte.MaxValue;
    // At most ten aim changes a second from a holder.
    private const float MinSendInterval = 0.1f;

    private static readonly Dictionary<ushort, byte> lastSent = new();
    private static float nextSend;

    public static void Reset() => lastSent.Clear();

    /// <summary>The holder's game: tells the server what the scanner points at, if that changed.</summary>
    internal static void SendAim(ShopScanner scanner)
    {
        if (!ShopRegisters.ServerOwned || scanner.scannerItem == null || !scanner.scannerItem.IsGrabbed() ||
            !NetworkedItem.TryGetNetworkedItem(scanner.scannerItem, out NetworkedItem item) || item == null || item.NetId == 0)
            return;

        byte shop = 0, module = NoModule;
        if (scanner.currentlyHoveredScanItem != null &&
            ShopRegisters.TryLocate(scanner.currentlyHoveredScanItem, out int shopIndex, out int moduleIndex))
        {
            shop = (byte)shopIndex;
            module = (byte)moduleIndex;
        }
        if (lastSent.TryGetValue(item.NetId, out byte sent) && sent == module)
            return;
        // A change inside the interval waits for the next hover change; clearing always goes.
        if (module != NoModule && Time.unscaledTime < nextSend)
            return;
        nextSend = Time.unscaledTime + MinSendInterval;
        lastSent[item.NetId] = module;
        ShopsClient.Current?.SendShopScannerAim(item.NetId, shop, module);
    }

    /// <summary>Another player's scanner points at <paramref name="moduleIndex"/> now: show it, as its holder sees it.</summary>
    public static void ShowAim(ushort scannerNetId, byte shopIndex, byte moduleIndex)
    {
        if (!NetworkedItem.TryGet(scannerNetId, out NetworkedItem item) || item == null ||
            !item.TryGetComponent(out ShopScanner scanner) || scanner.scannerItem == null || scanner.scannerItem.IsGrabbed())
            return;

        ScanItemCashRegisterModule module = null;
        if (moduleIndex != NoModule)
            ShopRegisters.TryGetModule(shopIndex, moduleIndex, out module);
        if (scanner.currentlyHoveredScanItem == module)
            return;
        scanner.currentlyHoveredScanItem = module;
        // Vanilla's own screen update; its HUD notification is only for the holder (it checks the grab).
        scanner.UpdateText();
        if (module != null && scanner.hoverSound != null)
            scanner.hoverSound.Play(scanner.transform.position, 1f, 1f, 0f, 1f, 10f, default, null, scanner.transform, false, 0f, null);
    }

    /// <summary>Every frame on each scanner: the laser shows while anyone holds it.</summary>
    internal static void ShowHolder(ShopScanner scanner, ShopScannerHolder holder)
    {
        if (scanner.scannerItem == null || scanner.laserBeam == null || scanner.scannerItem.IsGrabbed())
            return;
        bool heldByOther = NetworkLifecycle.Instance?.Avatars?.Players
            .Any(player => player != null && player.HoldsItem(scanner.gameObject)) == true;
        if (heldByOther == holder.ShowingRemote)
            return;
        holder.ShowingRemote = heldByOther;
        // Vanilla aims a held laser along this game's camera; another player's points the way the scanner does.
        scanner.laserBeam.EnableBeam(heldByOther, disableLaserPositionUpdate: true);
        scanner.laserBeam.transform.localRotation = Quaternion.identity;
        if (!heldByOther && scanner.currentlyHoveredScanItem != null)
        {
            scanner.currentlyHoveredScanItem = null;
            scanner.UpdateText();
        }
    }
}

/// <summary>Added to each shop scanner: shows another player's hold of it (<see cref="ShopScannerView.ShowHolder"/>).</summary>
public class ShopScannerHolder : MonoBehaviour
{
    private ShopScanner scanner;
    internal bool ShowingRemote;

    private void Awake() => scanner = GetComponent<ShopScanner>();

    private void Update()
    {
        if (scanner != null && ShopRegisters.ServerOwned)
            ShopScannerView.ShowHolder(scanner, this);
    }
}

[HarmonyPatch(typeof(ShopScanner))]
public static class ShopScannerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch("Start")]
    private static void Start(ShopScanner __instance) => __instance.gameObject.AddComponent<ShopScannerHolder>();

    // Vanilla updates the screen whenever the hovered item changes, and when the holder lets go.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(ShopScanner.UpdateText))]
    private static void UpdateText(ShopScanner __instance) => ShopScannerView.SendAim(__instance);
}
