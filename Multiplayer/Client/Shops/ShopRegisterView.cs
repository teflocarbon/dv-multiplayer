using System.Collections.Generic;
using System.Linq;
using DV.Booklets;
using DV.CashRegister;
using DV.Shops;
using DV.UI;
using DV.UIFramework;
using DV.Utils;
using Multiplayer.Core.Shops;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Shops;
using UnityEngine;

namespace Multiplayer.Client.Shops;

/// <summary>
/// A shop register on this game, the host's included: it shows the server's state
/// (<c>Server/Shops/ServerShops</c>). The basket's units go into vanilla's modules and the cash
/// into <c>DepositedCash</c>, so vanilla's screens show them; each change's event plays its sound
/// at the register, as vanilla would have on the game that did it (docs/design/shops.md).
/// </summary>
public static class ShopRegisterView
{
    private static readonly Dictionary<int, uint> revisions = new();
    private static readonly Dictionary<int, ClientboundShopRegisterStatePacket> states = new();

    /// <summary>The basket revision this game last showed for a shop, which a Buy names.</summary>
    public static uint Revision(int shopIndex) => revisions.TryGetValue(shopIndex, out uint revision) ? revision : 0;

    public static void Reset()
    {
        revisions.Clear();
        states.Clear();
    }

    public static void Apply(ClientboundShopRegisterStatePacket state, byte localPlayerId)
    {
        if (!ShopRegisters.TryGetShop(state.ShopIndex, out Shop shop) || shop.cashRegister == null)
            return;

        // An event with nothing changed (a refused scan, too little cash) comes with the same revision.
        bool newer = !revisions.TryGetValue(state.ShopIndex, out uint seen) || state.Revision > seen;
        states.TryGetValue(state.ShopIndex, out ClientboundShopRegisterStatePacket previous);
        CashRegisterWithModules register = shop.cashRegister;
        if (newer)
        {
            revisions[state.ShopIndex] = state.Revision;
            states[state.ShopIndex] = state;
            ShowBasket(shop, state);
            ShowCash(register, state.Deposited, (ShopRegisterEvent)state.Event == ShopRegisterEvent.CashAdded);
            register.IsProcessingTransaction = state.Delivering;
        }
        Play(shop, state, previous, localPlayerId);
    }

    private static void ShowBasket(Shop shop, ClientboundShopRegisterStatePacket state)
    {
        ScanItemCashRegisterModule[] modules = shop.scanItemResourceModules;
        for (int m = 0; m < modules.Length; m++)
        {
            if (modules[m] == null)
                continue;
            int units = 0;
            for (int i = 0; i < state.LineModules.Length; i++)
                if (state.LineModules[i] == m)
                    units += state.LineUnits[i];
            // Raises OnUnitsToBuyChanged: the register updates its total and prompt.
            modules[m].SetUnitsToBuy(units);
        }
    }

    // SetCash plays the add-cash sound and updates the screens; any other change (a join, Buy,
    // returned cash) only updates them, as vanilla's own removals do.
    private static void ShowCash(CashRegisterWithModules register, double deposited, bool added)
    {
        if (register.DepositedCash == deposited)
            return;
        if (added)
            register.SetCash(deposited);
        else
        {
            register.DepositedCash = deposited;
            register.textController?.UpdateCashText();
            register.textController?.UpdateInfoText();
        }
    }

    private static void Play(Shop shop, ClientboundShopRegisterStatePacket state, ClientboundShopRegisterStatePacket previous, byte localPlayerId)
    {
        CashRegisterWithModules register = shop.cashRegister;
        Transform at = register.transform;
        bool mine = state.EventPlayerId == localPlayerId;
        switch ((ShopRegisterEvent)state.Event)
        {
            case ShopRegisterEvent.Scanned when !mine:
                // The scanner played it for its holder already.
                PlayAtScanner(shop, scanner => scanner.scanSound);
                break;
            case ShopRegisterEvent.ScanRefused:
                PlayAtScanner(shop, scanner => scanner.errorSound);
                break;
            case ShopRegisterEvent.Bought:
                register.buyAudio?.Play(at.position, 1f, 1f, 0f, 1f, 500f, default, null, at, false, 0f, null);
                register.printerController?.Print(ignoreCooldown: true);
                // The presser's game prints the receipt, as before (server-made receipts are shops PR 3).
                if (mine && previous != null)
                    PrintReceipt(shop, previous);
                break;
            case ShopRegisterEvent.NotEnoughCash:
                register.notEnoughMoneyAudio?.Play(at.position, 1f, 1f, 0f, 1f, 500f, default, null, at, false, 0f, null);
                break;
            case ShopRegisterEvent.Cancelled:
                register.cancelAudio?.Play(at.position, 1f, 1f, 0f, 1f, 500f, default, null, at, false, 0f, null);
                break;
        }
    }

    private static void PlayAtScanner(Shop shop, System.Func<ShopScanner, AudioClip> clip)
    {
        ShopScanner scanner = Object.FindObjectsOfType<ShopScanner>()
            .OrderBy(s => (s.transform.position - shop.cashRegister.transform.position).sqrMagnitude)
            .FirstOrDefault();
        AudioClip sound = scanner != null ? clip(scanner) : null;
        if (sound != null)
            sound.Play(scanner.transform.position, 1f, 1f, 0f, 1f, 10f, default, null, scanner.transform, false, 0f, null);
    }

    private static void PrintReceipt(Shop shop, ClientboundShopRegisterStatePacket bought)
    {
        List<CashRegisterModule.CashRegisterModuleData> lines = new();
        for (int i = 0; i < bought.LineModules.Length; i++)
        {
            if (!ShopRegisters.TryGetModule(bought.ShopIndex, bought.LineModules[i], out ScanItemCashRegisterModule module))
                continue;
            lines.Add(new CashRegisterModule.CashRegisterModuleData(module.Data, copyPrice: true) { unitsToBuy = bought.LineUnits[i] });
        }
        if (lines.Count == 0)
            return;
        Transform anchor = shop.cashRegister.printerController.spawnAnchor;
        BookletCreator_CashRegisterReceipt.Create(lines, anchor.position, anchor.rotation, WorldMover.OriginShiftParent);
    }

    /// <summary>The server refused this player's request: why, on their own HUD.</summary>
    public static void ShowRefusal(ShopRefusal refusal)
    {
        string text = ShopRefusals.Describe(refusal);
        if (string.IsNullOrEmpty(text))
            return;
        SingletonBehaviour<ACanvasController<CanvasController.ElementType>>.Instance?.NotificationManager
            .ShowNotification(text, null, 4f, clearExisting: false, null, localize: false);
    }
}
