using DV;
using DV.WeatherSystem;
using LiteNetLib;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;
using Multiplayer.Shared.World;
using System;

namespace Multiplayer.Server.Session;

// NetworkServer's world handlers and sends (see docs/design/code-layout.md).
public partial class NetworkServer
{
    /// <summary>A time skip for every game, this server's own client included (/time).</summary>
    public void SendTimeAdvance(float seconds) =>
        SendPacketToAll(new ClientboundTimeAdvancePacket { amountOfTimeToSkipInSeconds = seconds },
            DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForWorldState);

    public void SendWeatherState(ITransportPeer peer = null)
    {
        var packet = WeatherDriver.Instance.GetSaveData(Globals.G.GameParams.WeatherEditorAlwaysAllowed).ToObject<ClientboundWeatherPacket>();

        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, excludeSelf: true);
    }

    public void SendGenericSwitchState(uint netId, bool isOn, ServerPlayer player = null)
    {
        var packet = new CommonGenericSwitchStatePacket
        {
            NetId = netId,
            IsOn = isOn
        };

        if (player != null)
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, deliveryMethod: DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, true);
    }

    private void OnServerboundTimeAdvancePacket(ServerboundTimeAdvancePacket packet, ITransportPeer peer)
    {
        if (packet.IsSleep)
        {
            OnSleepRequest(packet, peer);
            return;
        }

        if (!fastTravelAdvancesTime)
        {
            TryGetServerPlayer(peer, out ServerPlayer player);
            LogWarning($"Player {player?.Username} sent a TimeAdvance request, but FastTravelAdvancesTime is disabled");
            return;
        }

        SendPacketToAll
        (
            new ClientboundTimeAdvancePacket
            {
                amountOfTimeToSkipInSeconds = packet.amountOfTimeToSkipInSeconds
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            peer
        );
    }

    // One sleep cooldown for everyone, kept by the host. Each game used to check only its own last
    // wake-up (and a client's started empty on every join), so players could sleep one after another
    // and skip as much time as they liked, making everyone's jobs late.
    private void OnSleepRequest(ServerboundTimeAdvancePacket packet, ITransportPeer peer)
    {
        TryGetServerPlayer(peer, out ServerPlayer player);
        BedSleepingController sleep = BedSleepingController.Instance;
        if (sleep == null || packet.amountOfTimeToSkipInSeconds <= 0 || packet.amountOfTimeToSkipInSeconds > 24 * 3600)
            return;

        // The host's own sleep has already happened here (vanilla checked the cooldown and set the
        // wake time): pass the time skip on and share the wake time.
        if (player != null && NetworkLifecycle.Instance.IsHost(player))
        {
            SendPacketToAll(new ClientboundTimeAdvancePacket { amountOfTimeToSkipInSeconds = packet.amountOfTimeToSkipInSeconds },
                DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForWorldState, peer);
            SendSleepState();
            return;
        }

        DateTime now = sleep.GetCurrentIngameTime();
        bool disabled = Globals.G.GameParams.SleepCooldownInHours < 0;
        double sinceWake = (now - sleep.lastWakeTime).TotalSeconds;
        double cooldown = sleep.GetMinimumTimeSinceLastWakeSeconds();
        if (disabled || sinceWake < cooldown)
        {
            LogWarning($"{player?.Username} tried to sleep, but {(disabled ? "sleeping is disabled" : $"the shared cooldown has {cooldown - sinceWake:N0}s left")}");
            // Their game skipped the time locally; the weather sync puts their clock back.
            SendSleepState(peer);
            SendWeatherState(peer);
            return;
        }

        SendPacketToAll(new ClientboundTimeAdvancePacket { amountOfTimeToSkipInSeconds = packet.amountOfTimeToSkipInSeconds },
            DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForWorldState, peer);
        sleep.lastWakeTime = now.AddSeconds(packet.amountOfTimeToSkipInSeconds);
        sleep.lastSleepDurationSeconds = (int)packet.amountOfTimeToSkipInSeconds;
        Log($"{player?.Username} slept {packet.amountOfTimeToSkipInSeconds / 3600f:N1}h; the next sleep for anyone is after {sleep.lastWakeTime.AddSeconds(sleep.GetMinimumTimeSinceLastWakeSeconds())}");
        SendSleepState();
    }

    public void SendHazmatTiles(byte[] data, ITransportPeer peer)
    {
        ClientboundHazmatTilesPacket packet = new() { Data = data };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTiles, excludeSelf: true);
    }

    private void OnServerboundHazmatIgnitePacket(ServerboundHazmatIgnitePacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HazmatSync.Server_Ignite(new HazmatSync.ServerPlayerIgnite(packet.GridCoords, packet.Strength, player.AbsoluteWorldPosition));
    }

    public void SendSleepState(ITransportPeer peer = null)
    {
        BedSleepingController sleep = BedSleepingController.Instance;
        if (sleep == null)
            return;
        ClientboundSleepStatePacket packet = new()
        {
            LastWakeTimeOADate = sleep.lastWakeTime.ToOADate(),
            LastSleepDurationSeconds = sleep.lastSleepDurationSeconds
        };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, excludeSelf: true);
    }

    private void OnCommonChangeJunctionPacket(CommonChangeJunctionPacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, peer);
    }

    private void OnCommonRotateTurntablePacket(CommonRotateTurntablePacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, peer);
    }

    private void OnCommonGenericSwitchStatePacket(CommonGenericSwitchStatePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            LogWarning($"Received Generic Switch State, but player was not found");
            return;
        }

        if (!NetworkedGenericSwitch.TryGet(packet.NetId, out NetworkedGenericSwitch netSwitch))
        {
            LogWarning($"Received Generic Switch State from \"{player.Username}\" for switch {packet.NetId}, but switch does not exist!");
            return;
        }

        netSwitch.Server_ReceiveSwitchState(packet.IsOn, player);
    }
}
