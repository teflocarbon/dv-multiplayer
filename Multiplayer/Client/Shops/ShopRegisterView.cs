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
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Shops;
using UnityEngine;

namespace Multiplayer.Client.Shops;

/// <summary>
/// A shop register on this game, the host's included: it shows the server's state
/// (<c>Server/Shops/ServerShops</c>) with this player's own requests the server hasn't answered yet
/// applied on top (<see cref="ShopPrediction"/>), so a player at 250 ms sees their scan, payment,
/// Buy and Cancel at once. The basket's units go into vanilla's modules and the cash into
/// <c>DepositedCash</c>, so vanilla's screens show them; each change's event plays its sound at the
/// register, as vanilla would have on the game that did it, except where this player's game already
/// played it when it predicted the change (docs/design/shops.md).
/// </summary>
public static class ShopRegisterView
{
    private static readonly Dictionary<int, uint> revisions = new();
    private static readonly Dictionary<int, ClientboundShopRegisterStatePacket> states = new();
    private static readonly Dictionary<int, ShopPrediction> predictions = new();
    private static uint lastRequestId;

    /// <summary>The basket revision this game last had from the server for a shop, which a Buy names.</summary>
    public static uint Revision(int shopIndex) => revisions.TryGetValue(shopIndex, out uint revision) ? revision : 0;

    public static void Reset()
    {
        revisions.Clear();
        states.Clear();
        predictions.Clear();
    }

    private static byte LocalPlayerId => NetworkLifecycle.Instance?.LocalSession?.PlayerId ?? 0;

    private static ShopPrediction Prediction(int shopIndex)
    {
        if (!predictions.TryGetValue(shopIndex, out ShopPrediction prediction))
            predictions[shopIndex] = prediction = new ShopPrediction();
        return prediction;
    }

    /// <summary>The register as this player's game shows it now.</summary>
    public static PredictedRegister Shown(int shopIndex)
    {
        states.TryGetValue(shopIndex, out ClientboundShopRegisterStatePacket state);
        ShopPrediction prediction = Prediction(shopIndex);
        prediction.Expire(Time.unscaledTime);
        return prediction.Predict(Lines(state), state?.Deposited ?? 0, state?.Delivering ?? false, LocalPlayerId,
            module => UnitPrice(shopIndex, module));
    }

    /// <summary>
    /// This player makes a request: it shows at once, and is kept until the server answers it.
    /// Returns the request's id, for the request packet.
    /// </summary>
    public static uint Predict(int shopIndex, ShopRequestKind kind, int module = -1, double amount = 0, bool cashSound = false)
    {
        uint id = ++lastRequestId;
        Prediction(shopIndex).Add(new PendingShopRequest(id, kind, module, amount, Time.unscaledTime));
        Show(shopIndex, cashSound);
        return id;
    }

    public static void Apply(ClientboundShopRegisterStatePacket state, byte localPlayerId)
    {
        if (!ShopRegisters.TryGetShop(state.ShopIndex, out Shop shop) || shop.cashRegister == null)
            return;

        // An event with nothing changed (a refused scan, too little cash) comes with the same revision.
        bool newer = !revisions.TryGetValue(state.ShopIndex, out uint seen) || state.Revision > seen;
        states.TryGetValue(state.ShopIndex, out ClientboundShopRegisterStatePacket previous);
        bool mine = state.EventPlayerId == localPlayerId;
        if (newer)
        {
            revisions[state.ShopIndex] = state.Revision;
            states[state.ShopIndex] = state;
        }
        if (state.AckPlayerId == localPlayerId && state.AckRequestId != 0)
            Prediction(state.ShopIndex).Acknowledge(state.AckRequestId);
        // This player's own payment rang when it was predicted.
        Show(state.ShopIndex, (ShopRegisterEvent)state.Event == ShopRegisterEvent.CashAdded && !mine);
        Play(shop, state, previous, mine);
    }

    /// <summary>The server refused this player's request: it leaves the prediction, and the reason shows on their HUD.</summary>
    public static void Refused(int shopIndex, uint requestId, ShopRequestKind request, ShopRefusal refusal)
    {
        Multiplayer.LogDebug(() => $"Shop {shopIndex}: request {requestId} ({request}) refused: {refusal}");
        Prediction(shopIndex).Refuse(requestId);
        Show(shopIndex, false);
        ShowRefusal(refusal);
    }

    // Writes what this player's game shows into vanilla's register.
    private static void Show(int shopIndex, bool cashSound)
    {
        if (!ShopRegisters.TryGetShop(shopIndex, out Shop shop) || shop.cashRegister == null)
            return;
        PredictedRegister shown = Shown(shopIndex);
        ScanItemCashRegisterModule[] modules = shop.scanItemResourceModules;
        for (int m = 0; m < modules.Length; m++)
            // Raises OnUnitsToBuyChanged: the register updates its total and prompt.
            modules[m]?.SetUnitsToBuy(shown.UnitsOf(m));
        ShowCash(shop.cashRegister, shown.Deposited, cashSound);
        shop.cashRegister.IsProcessingTransaction = shown.Delivering;
    }

    private static IEnumerable<ShopBasketLine> Lines(ClientboundShopRegisterStatePacket state)
    {
        if (state?.LineModules == null)
            yield break;
        for (int i = 0; i < state.LineModules.Length; i++)
            yield return new ShopBasketLine(state.LineModules[i], state.LineOwners[i], state.LineUnits[i]);
    }

    private static double UnitPrice(int shopIndex, int module) =>
        ShopRegisters.TryGetModule(shopIndex, module, out ScanItemCashRegisterModule m) ? m.Data.pricePerUnit : 0;

    // SetCash plays the add-cash sound and updates the screens; any other change (a join, Buy,
    // returned cash) only updates them, as vanilla's own removals do.
    private static void ShowCash(CashRegisterWithModules register, double deposited, bool added)
    {
        if (register.DepositedCash == deposited)
            return;
        if (added && deposited > register.DepositedCash)
            register.SetCash(deposited);
        else
        {
            register.DepositedCash = deposited;
            register.textController?.UpdateCashText();
            register.textController?.UpdateInfoText();
        }
    }

    private static void Play(Shop shop, ClientboundShopRegisterStatePacket state, ClientboundShopRegisterStatePacket previous, bool mine)
    {
        CashRegisterWithModules register = shop.cashRegister;
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
                if (!mine)
                    PlayBuy(register);
                // The presser's game prints the receipt, as before (server-made receipts are shops PR 3).
                if (mine && previous != null)
                    PrintReceipt(shop, previous);
                break;
            case ShopRegisterEvent.NotEnoughCash:
                PlayAtRegister(register, register.notEnoughMoneyAudio);
                break;
            case ShopRegisterEvent.Cancelled when !mine:
                PlayAtRegister(register, register.cancelAudio);
                break;
        }
    }

    /// <summary>The buy sound and the printer, at the register.</summary>
    public static void PlayBuy(CashRegisterWithModules register)
    {
        PlayAtRegister(register, register.buyAudio);
        register.printerController?.Print(ignoreCooldown: true);
    }

    public static void PlayAtRegister(CashRegisterWithModules register, AudioClip clip)
    {
        Transform at = register.transform;
        clip?.Play(at.position, 1f, 1f, 0f, 1f, 500f, default, null, at, false, 0f, null);
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
