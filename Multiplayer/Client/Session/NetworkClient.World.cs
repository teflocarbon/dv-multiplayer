using DV;
using DV.Customization;
using DV.WeatherSystem;
using LiteNetLib;
using Multiplayer.Client.World;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.World;
using Newtonsoft.Json.Linq;
using System;

namespace Multiplayer.Client.Session;

// NetworkClient's world handlers and sends (see docs/design/code-layout.md).
public partial class NetworkClient : IWorldClient
{
    private void OnClientboundWeatherPacket(ClientboundWeatherPacket packet)
    {
        if (LoadingState < PlayerLoadingState.Complete)
            Log("Received weather state");

        // The host's clock arrives one trip late, so applying it as-is stepped the client's clock
        // back a little every update. Time-driven consumers read a backward step as a whole day
        // passing (a lit flashlight's battery emptied at once). Keep a clock that is only slightly
        // ahead; real changes (the host setting the time) are far larger.
        if (LoadingState >= PlayerLoadingState.Complete && WeatherDriver.Instance.manager != null)
        {
            double local = WeatherDriver.Instance.manager.RealDateTime.ToOADate();
            double behind = (local - packet.OADate) * TimeSpan.FromDays(1).TotalSeconds;
            if (behind > 0 && behind < MaxWeatherClockLeadSeconds)
                packet.OADate = local;
        }

        WeatherDriver.Instance.LoadSaveData(JObject.FromObject(packet), Globals.G.GameParams.WeatherEditorAlwaysAllowed);
    }

    private void OnClientboundTimeAdvancePacket(ClientboundTimeAdvancePacket packet)
    {
        TimeAdvance.AdvanceTime(packet.amountOfTimeToSkipInSeconds);
    }

    private void OnClientboundRailwayStatePacket(ClientboundRailwayStatePacket packet)
    {
        Log("Received railway state");

        // A junction or turntable missing on this side used to return here without marking the
        // railway loaded, and the join then waited for it forever. Apply what exists and carry on.
        int missingJunctions = 0, missingTurntables = 0;
        for (int i = 0; i < packet.SelectedJunctionBranches.Length; i++)
        {
            if (!NetworkedJunction.Get((ushort)(i + 1), out NetworkedJunction junction))
            {
                missingJunctions++;
                continue;
            }
            junction.Switch((byte)Junction.SwitchMode.NO_SOUND, packet.SelectedJunctionBranches[i], true);
        }

        for (int i = 0; i < packet.TurntableRotations.Length; i++)
        {
            if (!NetworkedTurntable.Get((byte)(i + 1), out NetworkedTurntable turntable))
            {
                missingTurntables++;
                continue;
            }
            turntable.SetRotation(packet.TurntableRotations[i], true, true);
        }

        if (missingJunctions > 0 || missingTurntables > 0)
            LogWarning($"Railway state: {missingJunctions} of {packet.SelectedJunctionBranches.Length} junctions and " +
                       $"{missingTurntables} of {packet.TurntableRotations.Length} turntables not found locally; " +
                       "those keep their local position (mismatched mods or game versions?)");

        railwayStateLoaded = true;
    }

    private void OnClientboundCustomizationsPacket(ClientboundCustomizationsPacket packet)
    {
        Log("Received customizations");

        WorldCustomization.I.ClearHoles();

        foreach (var hole in packet.Holes)
            WorldCustomization.I.AddHole(hole.Position, hole.Rotation);
    }

    private void OnCommonChangeJunctionPacket(CommonChangeJunctionPacket packet)
    {
        if (!NetworkedJunction.Get(packet.NetId, out NetworkedJunction junction))
            return;
        junction.Switch(packet.Mode, packet.SelectedBranch);
    }

    private void OnCommonRotateTurntablePacket(CommonRotateTurntablePacket packet)
    {
        if (!NetworkedTurntable.Get(packet.NetId, out NetworkedTurntable turntable))
            return;
        turntable.SetRotation(packet.rotation);
    }

    private void OnCommonGenericSwitchStatePacket(CommonGenericSwitchStatePacket packet)
    {
        if (!NetworkedGenericSwitch.TryGet(packet.NetId, out NetworkedGenericSwitch netSwitch))
        {
            LogWarning($"Received Generic Switch State for switch {packet.NetId}, but switch does not exist!");
            return;
        }

        netSwitch.Apply(packet.IsOn);
    }

    public void SendTimeAdvance(float amountOfTimeToSkipInSeconds, bool isSleep = false)
    {
        SendPacketToServer(new ServerboundTimeAdvancePacket
        {
            amountOfTimeToSkipInSeconds = amountOfTimeToSkipInSeconds,
            IsSleep = isSleep
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnClientboundHazmatTilesPacket(ClientboundHazmatTilesPacket packet)
    {
        HazmatClient.Apply(packet.Data);
    }

    public void SendHazmatIgnite(int gridCoords, float strength)
    {
        SendPacketToServer(new ServerboundHazmatIgnitePacket { GridCoords = gridCoords, Strength = strength }, DeliveryMethod.ReliableOrdered);
    }

    private void OnClientboundSleepStatePacket(ClientboundSleepStatePacket packet)
    {
        BedSleepingController sleep = BedSleepingController.Instance;
        if (sleep == null)
            return;
        sleep.lastWakeTime = DateTime.FromOADate(packet.LastWakeTimeOADate);
        sleep.lastSleepDurationSeconds = packet.LastSleepDurationSeconds;
    }

    public void SendJunctionSwitched(ushort netId, byte selectedBranch, Junction.SwitchMode mode)
    {
        SendPacketToServer(new CommonChangeJunctionPacket
        {
            NetId = netId,
            SelectedBranch = selectedBranch,
            Mode = (byte)mode
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendTurntableRotation(byte netId, float rotation)
    {
        SendPacketToServer(new CommonRotateTurntablePacket
        {
            NetId = netId,
            rotation = rotation
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendGenericSwitchState(uint netId, bool isOn)
    {
        SendPacketToServer
        (
            new CommonGenericSwitchStatePacket
            {
                NetId = netId,
                IsOn = isOn
            },
            deliveryMethod: DeliveryMethod.ReliableOrdered
        );
    }
}
