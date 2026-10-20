using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Session;
using System;
using System.Collections.Generic;
using static UnityModManagerNet.UnityModManager;

namespace Multiplayer.Client.Api
{
    /// <summary>
    ///     MPAPI's client API: the player at this keyboard, on a remote client and on a host alike
    ///     (<see cref="IClientApiSession"/>).
    /// </summary>
    public class ClientAPIProvider : IClient
    {
        private readonly IClientApiSession client;
        private readonly PlayerAvatars avatars;
        private readonly Dictionary<byte, ClientPlayerWrapper> wrappers = [];

        public event Action<IPlayer> OnPlayerConnected;
        public event Action<IPlayer> OnPlayerDisconnected;

        public void RegisterReadyBlock(ModInfo modInfo)
        {
            client.RegisterReadyBlock(modInfo.DisplayName);
        }

        public void CancelReadyBlock(ModInfo modInfo)
        {
            client.CancelReadyBlock(modInfo.DisplayName);
        }

        #region Client Properties
        public byte PlayerId => client.PlayerId;
        public IReadOnlyCollection<IPlayer> Players => wrappers.Values;
        public int PlayerCount => (NetworkLifecycle.Instance.Avatars?.Players.Count ?? 0) + 1; // add 1 for local player

        public IPlayer GetPlayer(byte playerId)
        {
            wrappers.TryGetValue(playerId, out var player);
            return player;
        }

        public bool IsConnected => client.IsRunning;

        public int Ping => client.Ping;

        #endregion

        #region Packet API
        public void RegisterPacket<T>(ClientPacketHandler<T> handler) where T : class, IPacket, new()
        {
            client.RegisterExternalPacket<T>(handler);
        }
        public void RegisterSerializablePacket<T>(ClientPacketHandler<T> handler) where T : class, ISerializablePacket, new()
        {
            client.RegisterExternalSerializablePacket<T>(handler);
        }

        public void SendPacketToServer<T>(T packet, bool reliable = true) where T : class, IPacket, new()
        {
            client.SendExternalPacketToServer(packet, reliable);
        }

        public void SendSerializablePacketToServer<T>(T packet, bool reliable = true) where T : class, ISerializablePacket, new()
        {
            client.SendExternalSerializablePacketToServer(packet, reliable);
        }
        #endregion

        #region Class Helpers
        internal ClientAPIProvider(IClientApiSession clientInstance)
        {
            this.client = clientInstance;

            avatars = NetworkLifecycle.Instance.Avatars;
            avatars.OnPlayerConnected += OnPlayerConnectedInternal;
            avatars.OnPlayerDisconnected += OnPlayerDisconnectedInternal;
            // Avatars the registry already has (a host's own session starts after its server).
            foreach (NetworkedPlayer player in avatars.Players)
                GetWrapper(player);
        }

        internal void Dispose()
        {
            avatars.OnPlayerConnected -= OnPlayerConnectedInternal;
            avatars.OnPlayerDisconnected -= OnPlayerDisconnectedInternal;
        }

        private ClientPlayerWrapper GetWrapper(NetworkedPlayer networkedPlayer)
        {
            if (!wrappers.TryGetValue(networkedPlayer.PlayerId, out var wrapper))
            {
                wrapper = new ClientPlayerWrapper(networkedPlayer);
                wrappers[networkedPlayer.PlayerId] = wrapper;
            }
            return wrapper;
        }

        private void OnPlayerConnectedInternal(NetworkedPlayer networkedPlayer)
        {
            OnPlayerConnected?.Invoke(GetWrapper(networkedPlayer));
        }

        private void OnPlayerDisconnectedInternal(NetworkedPlayer networkedPlayer)
        {
            OnPlayerDisconnected?.Invoke(GetWrapper(networkedPlayer));
            wrappers.Remove(networkedPlayer.PlayerId);
        }
        #endregion
    }
}
