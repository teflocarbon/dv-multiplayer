using DV.CashRegister;
using DV.InventorySystem;
using Multiplayer.Server.Players;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using System;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Stations;

/// <summary>
/// The server's part of a cash register with modules (beside its
/// <see cref="NetworkedCashRegisterWithModules"/>): carries out players' deposits, buys and
/// cancels, and tells the players at its service station. Added by
/// <c>NetworkServer.InitialiseStations</c>; its station's <see cref="ServerPitStopStation"/> gives
/// it the players in range.
/// </summary>
public class ServerCashRegister : MonoBehaviour
{
    public NetworkedCashRegisterWithModules Register { get; private set; }
    private CashRegisterWithModules CashRegister => Register.CashRegister;

    private CullingManager cullingManager;

    private static NetworkServer Server => NetworkLifecycle.Instance.Server;

    public static bool TryGet(ushort netId, out ServerCashRegister server)
    {
        server = null;
        return NetworkedCashRegisterWithModules.Get(netId, out NetworkedCashRegisterWithModules register) &&
               register.TryGetComponent(out server);
    }

    // Called by hand, once: the register can be inactive, and Unity only runs Awake once it's active.
    public void Setup()
    {
        Register = GetComponent<NetworkedCashRegisterWithModules>();
    }

    protected void OnDestroy()
    {
        if (cullingManager != null)
            cullingManager.PlayerEnteredActivationRegion -= OnPlayerEnteredActivationRegion;
    }

    /// <summary>The players at this register's service station.</summary>
    public void Init(CullingManager cullingManager)
    {
        if (cullingManager == null)
            return;

        Multiplayer.LogDebug(() => $"ServerCashRegister.Init({CashRegister.GetObjectPath()})");
        this.cullingManager = cullingManager;
        this.cullingManager.PlayerEnteredActivationRegion += OnPlayerEnteredActivationRegion;
    }

    private void OnPlayerEnteredActivationRegion(ServerPlayer serverPlayer)
    {
        Multiplayer.LogDebug(() => $"ServerCashRegister.OnPlayerEnteredActivationRegion({serverPlayer.Username}) deposited cash: {CashRegister.DepositedCash}");
        if (CashRegister.DepositedCash > 0f)
        {
            Server.SendCashRegisterAction
                (
                    new CommonCashRegisterWithModulesActionPacket
                    {
                        NetId = Register.NetId,
                        Action = CashRegisterAction.SetFunds,
                        Amount = CashRegister.DepositedCash
                    },
                    [serverPlayer]
                );
        }
    }

    /// <summary>
    /// A player's request. The host's own player gets its answer in-process
    /// (<see cref="NetworkedCashRegisterWithModules.AnswerLocalRequest"/>), on every path, so its
    /// request finishes on it instead of waiting out the timeout.
    /// </summary>
    public void ProcessAction(ServerPlayer player, CommonCashRegisterWithModulesActionPacket packet)
    {
        bool success = false;
        CashRegisterAction response = CashRegisterAction.RejectGeneric;
        bool local = player == Server.LocalPlayer;

        Server.LogDebug(() => $"ServerCashRegister.ProcessAction({player.Username}, {packet.Action}, {packet.Amount})");
        if (transform.PlayerCanReach(player, 1))
        {
            Register.ProcessingAction = true;
            switch (packet.Action)
            {
                case CashRegisterAction.Cancel:
                    CashRegister?.Cancel();
                    success = true;

                    break;

                case CashRegisterAction.Buy:

                    Multiplayer.LogDebug(() => $"ServerCashRegister.ProcessAction({packet.Action}) Player Money: {Inventory.Instance.PlayerMoney}, TotalCost: {CashRegister.GetTotalCost()}, TotalUnitsInBasket: {CashRegister.TotalUnitsInBasket()}");

                    if (CashRegister.TotalUnitsInBasket() <= 0)
                    {
                        response = CashRegisterAction.RejectedNoItems;
                    }
                    else if (CashRegister.DepositedCash < CashRegister.GetTotalCost())
                    {
                        response = CashRegisterAction.RejectFunds;
                    }
                    else
                    {
                        success = CashRegister?.Buy() ?? false;
                    }

                    Multiplayer.LogDebug(() => $"ServerCashRegister.ProcessAction({packet.Action}, {packet.Amount}) Response: {response}, Buy success: {success}, Player Money: {Inventory.Instance.PlayerMoney}, TotalCost: {CashRegister.GetTotalCost()}, TotalUnitsInBasket: {CashRegister.TotalUnitsInBasket()}");

                    break;

                case CashRegisterAction.AddCash:

                    double remainingCost = CashRegister.GetRemainingCost();

                    if (remainingCost <= 0)
                    {
                        Multiplayer.LogDebug(() => $"ServerCashRegister.ProcessAction({packet.Action}) No remaining cost to add cash for.");
                        Register.ProcessingAction = false;
                        // Nothing to do, and nothing changed for the players at the station
                        if (local)
                            Register.AnswerLocalRequest(CashRegisterAction.SetFunds);
                        return;
                    }
                    else if (CashRegister.TotalUnitsInBasket() <= 0)
                    {
                        Multiplayer.LogDebug(() => $"ServerCashRegister.ProcessAction({packet.Action}) No items in basket to add cash for.");
                        response = CashRegisterAction.RejectedNoItems;
                        success = false;
                    }
                    else
                    {
                        double amountToAdd = Math.Min(remainingCost, Inventory.Instance.PlayerMoney);

                        Inventory.Instance.RemoveMoney(amountToAdd);
                        CashRegister.SetCash(CashRegister.DepositedCash + amountToAdd);

                        Server.LogDebug(() => $"ServerCashRegister.ProcessAction({packet.Action}) Added cash: {amountToAdd}, New DepositedCash: {CashRegister.DepositedCash}, Player Money: {Inventory.Instance.PlayerMoney}");
                        packet.Action = CashRegisterAction.SetFunds;
                        packet.Amount = CashRegister.DepositedCash;
                        success = true;
                    }

                    break;

                case CashRegisterAction.SetFunds:
                    //Server.LogDebug(() => $"ServerCashRegister.ProcessAction({player.Username}, {packet.Action}, {packet.Amount}) Wallet: {Inventory.Instance.PlayerMoney}");
                    break;
            }
        }
        else
        {
            Server.LogDebug(() => $"Player \"{player.Username}\" tried to interact with Cash Register , but they are too far away");
        }

        if (success)
            Server.SendCashRegisterAction(packet, cullingManager?.ActivePlayers?.ToArray());
        else if (!local)
            Server.SendCashRegisterAction
                (
                    new CommonCashRegisterWithModulesActionPacket
                    {
                        NetId = Register.NetId,
                        Action = response,
                        Amount = CashRegister.DepositedCash
                    },
                    [player]
                );

        // The host's own player never gets these packets: the station's players don't include it,
        // and a refusal sent to its own client used to empty the register's modules there.
        if (local)
            Register.AnswerLocalRequest(success ? packet.Action : response);

        Register.ProcessingAction = false;
    }
}
