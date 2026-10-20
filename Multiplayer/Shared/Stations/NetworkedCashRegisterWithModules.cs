using DV.CashRegister;
using DV.Interaction;
using DV.InventorySystem;
using DV.Shops;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Stations;

public class NetworkedCashRegisterWithModules : IdMonoBehaviour<ushort, NetworkedCashRegisterWithModules>
{
    #region Lookup Cache
    private static readonly Dictionary<CashRegisterWithModules, NetworkedCashRegisterWithModules> cashRegisterToNetworkedCashRegister = [];

    public static bool Get(ushort netId, out NetworkedCashRegisterWithModules obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedCashRegisterWithModules> rawObj);
        obj = (NetworkedCashRegisterWithModules)rawObj;
        return b;
    }

    public static bool TryGet(CashRegisterWithModules cashRegister, out NetworkedCashRegisterWithModules networkedCashRegisterWithModules)
    {
        return cashRegisterToNetworkedCashRegister.TryGetValue(cashRegister, out networkedCashRegisterWithModules);
    }

    public static void InitialiseCashRegisters()
    {
        // Shop registers are the server's alone (Server/Shops/ServerShops): not numbered here.
        var shopRegisters = GlobalShopController.Instance.globalShopList
            .Select(shop => shop.cashRegister)
            .ToArray();

        //Find all CashRegistersWithModules that are placed on the map
        //sort them by their hierarchy path for consistent ordering
        var registers = CashRegisterBase.allCashRegisters
            .OfType<CashRegisterWithModules>()
            .Where(r => !shopRegisters.Contains(r))
            .OrderBy(r => r.transform.position.x)
            .ThenBy(r => r.transform.position.y)
            .ThenBy(r => r.transform.position.z)
            .ToArray();

        //Multiplayer.LogDebug(() => $"InitialiseCashRegisters() Found: {registers?.Length}");

        foreach (var register in registers)
        {
            var netRegister = register.GetOrAddComponent<NetworkedCashRegisterWithModules>();
            netRegister.CashRegister = register;

            if (netRegister.NetId == 0)
                netRegister.Awake();

            cashRegisterToNetworkedCashRegister[register] = netRegister;

            //Multiplayer.LogDebug(() => $"InitialiseCashRegisters() Register: {register?.GetObjectPath()}, netId: {netRegister.NetId}");
        }
    }

    /// <summary>Every module register on the map, once <see cref="InitialiseCashRegisters"/> has run.</summary>
    public static IReadOnlyCollection<NetworkedCashRegisterWithModules> All => cashRegisterToNetworkedCashRegister.Values;

    #endregion

    protected override bool IsIdServerAuthoritative => false;

    #region Server Variables
    /// <summary>
    /// True while the server carries out a player's request here (<c>Server/Stations/ServerCashRegister</c>):
    /// vanilla moving money as part of it isn't a new request.
    /// </summary>
    public bool ProcessingAction { get; set; }
    #endregion

    #region Client Variables
    public bool IsBusy => isBuying || isCancelling || isAddingCash || ProcessingAction;
    bool isBuying;
    bool isCancelling;
    bool isAddingCash;

    double pendingCashToAdd = 0;
    #endregion

    #region Common Variables
    public CashRegisterWithModules CashRegister { get; private set; }
    #endregion

    #region Unity

    protected override void Awake()
    {
        //Multiplayer.LogDebug(()=>$"CashRegisterWithModules.Awake() {transform.GetObjectPath()}, {transform.position}, netId: {NetId}");

        if (NetId == 0)
            base.Awake();
    }

    protected override void OnDestroy()
    {
        cashRegisterToNetworkedCashRegister.Remove(CashRegister);

        base.OnDestroy();
    }
    #endregion

    #region Server

    /// <summary>
    /// The server's answer to this game's own request, when this game is the server: the host's
    /// player gets no packet, so the request finishes here. Only ends the wait: the register's
    /// state is the server's already.
    /// </summary>
    public void AnswerLocalRequest(CashRegisterAction action)
    {
        Multiplayer.LogDebug(() => $"NetworkedCashRegisterWithModules.AnswerLocalRequest({action}) isBuying: {isBuying}, isCancelling: {isCancelling}, isAddingCash: {isAddingCash}");

        isBuying = false;
        isCancelling = false;
        isAddingCash = false;
        pendingCashToAdd = 0;

        if (action == CashRegisterAction.RejectFunds)
            CashRegister?.notEnoughMoneyAudio?.Play(CashRegister.transform.position, 1f, 1f, 0f, 1f, 500f, default, null, CashRegister.transform, false, 0f, null);
    }

    #endregion

    #region Client

    public void Client_ProcessCashRegisterAction(CashRegisterAction action, double amount)
    {
        Multiplayer.LogDebug(() => $"NetworkedCashRegisterWithModules.Client_ProcessCashRegisterAction({action}, {amount}) isBuying: {isBuying}, isCancelling: {isCancelling}");
        switch (action)
        {
            case CashRegisterAction.Cancel:

                isCancelling = false;
                isBuying = false;

                foreach (var module in CashRegister.registerModules)
                    module.ResetData();

                CashRegister.OnUnitsToBuyChanged();

                if (CashRegister.DepositedCash > 0)
                {
                    CashRegister?.cancelAudio?.Play(CashRegister.transform.position, 1f, 1f, 0f, 1f, 500f, default, null, CashRegister.transform, false, 0f, null);
                    CashRegister.DepositedCash = 0;
                    CashRegister?.OnDepositedUpdated();
                }

                //CashRegister?.Cancel();

                break;

            case CashRegisterAction.Buy:

                isCancelling = false;
                isBuying = false;

                CashRegister?.buyAudio?.Play(CashRegister.transform.position, 1f, 1f, 0f, 1f, 500f, default, null, CashRegister.transform, false, 0f, null);

                foreach (var module in CashRegister.registerModules)
                    module.ResetData();

                CashRegister?.OnUnitsToBuyChanged();

                CashRegister.DepositedCash = 0;
                CashRegister?.OnDepositedUpdated();

                CashRegister.IsProcessingTransaction = false;

                break;

            case CashRegisterAction.AddCash:
                break;

            case CashRegisterAction.SetFunds:
                Multiplayer.LogDebug(() => $"NetworkedCashRegisterWithModules.Client_ProcessCashRegisterAction({action}, {amount}) Setting deposited cash.");
                CashRegister?.SetCash(amount);

                break;

            case CashRegisterAction.RejectGeneric:
                isBuying = false;
                isCancelling = false;

                //if (isAddingCash)
                //{
                    //Inventory.Instance.AddMoney(pendingCashToAdd);
                    pendingCashToAdd = 0;
                //}

                isAddingCash = false;

                break;

            case CashRegisterAction.RejectFunds:
                isBuying = false;
                isCancelling = false;

                //if (isAddingCash)
                //{
                    //Inventory.Instance.AddMoney(pendingCashToAdd);
                    pendingCashToAdd = 0;
                //}

                isAddingCash = false;

                CashRegister?.notEnoughMoneyAudio?.Play(CashRegister.transform.position, 1f, 1f, 0f, 1f, 500f, default, null, CashRegister.transform, false, 0f, null);

                break;

            case CashRegisterAction.RejectedNoItems:
                isBuying = false;
                isCancelling = false;

                //if (isAddingCash)
                //{
                    //Inventory.Instance.AddMoney(pendingCashToAdd);
                    pendingCashToAdd = 0;
                //}

                isAddingCash = false;

                foreach (var module in CashRegister.registerModules)
                    module.ResetData();

                CashRegister?.OnUnitsToBuyChanged();

                // The server's deposited cash, which a refusal doesn't touch: setting 0 here emptied
                // this game's register while the server's (and everyone else's) kept the cash.
                CashRegister.DepositedCash = amount;
                CashRegister?.OnDepositedUpdated();

                CashRegister?.buyAudio?.Play(CashRegister.transform.position, 1f, 1f, 0f, 1f, 500f, default, null, CashRegister.transform, false, 0f, null);
                break;
        }
    }

    // On the host the server answers in-process at its next poll (AnswerLocalRequest); its player's
    // session waits for that with a backstop of its own (LocalPlayerSession.RPC_Timeout).
    private static float RequestTimeout => StationsClient.Current?.RPC_Timeout ?? 0f;

    public IEnumerator Buy()
    {
        if (isBuying || isCancelling || NetworkLifecycle.Instance.IsProcessingPacket)
            yield break;

        DisableInteraction();
        CashRegister.IsProcessingTransaction = true;

        StationsClient.Current?.SendCashRegisterAction(NetId, CashRegisterAction.Buy);

        isBuying = true;
        float timeOut = Time.time + RequestTimeout;

        yield return new WaitUntil(() => Time.time >= timeOut || isBuying == false);

        isBuying = false;

        CashRegister.IsProcessingTransaction = false;
        EnableInteraction();
    }

    public IEnumerator Cancel()
    {
        if (isBuying || isCancelling || isAddingCash || NetworkLifecycle.Instance.IsProcessingPacket)
            yield break;

        DisableInteraction();

        StationsClient.Current?.SendCashRegisterAction(NetId, CashRegisterAction.Cancel);

        isCancelling = true;
        float timeOut = Time.time + RequestTimeout;

        yield return new WaitUntil(() => Time.time >= timeOut || isCancelling == false);

        isCancelling = false;

        EnableInteraction();
    }

    public IEnumerator AddCash(double amount)
    {
        if (isBuying || isCancelling || isAddingCash || ProcessingAction || NetworkLifecycle.Instance.IsProcessingPacket)
            yield break;

        DisableInteraction();

        Multiplayer.LogDebug(() => $"NetworkedCashRegisterWithModules.AddCash({amount}) Sending AddCash action.");
        StationsClient.Current?.SendCashRegisterAction(NetId, CashRegisterAction.AddCash);

        isAddingCash = true;
        pendingCashToAdd = amount;

        float timeOut = Time.time + RequestTimeout;

        Multiplayer.LogDebug(() => $"NetworkedCashRegisterWithModules.AddCash({amount}) Waiting");
        yield return new WaitUntil(() => Time.time >= timeOut || isAddingCash == false);

        Multiplayer.LogDebug(() => $"NetworkedCashRegisterWithModules.AddCash({amount}) Wait complete, time-out: {Time.time >= timeOut}, isAddingCash: {isAddingCash}");

        pendingCashToAdd = 0;
        isAddingCash = false;

        EnableInteraction();
    }

    private void DisableInteraction()
    {
        CashRegister.buyButton.InteractionAllowed = false;
        CashRegister.cancelButton.InteractionAllowed = false;
    }

    private void EnableInteraction()
    {
        CashRegister.buyButton.InteractionAllowed = true;
        CashRegister.cancelButton.InteractionAllowed = true;
    }

    #endregion

    #region Common

    #endregion
}
