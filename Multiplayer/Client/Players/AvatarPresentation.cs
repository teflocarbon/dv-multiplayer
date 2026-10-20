using Multiplayer.Shared.Players;
using UnityEngine;

namespace Multiplayer.Client.Players;

/// <summary>Dresses other players' avatars: the name tag over them, their character model and ping.</summary>
public class AvatarPresentation : IAvatarPresentation
{
    private readonly GameObject playerTagPrefab = Multiplayer.AssetIndex.PlayerTag;

    public void AddNameTag(NetworkedPlayer player)
    {
        Object.Instantiate(playerTagPrefab, player.transform);
        player.gameObject.AddComponent<NetworkedPlayerNameTag>();
    }

    public void ChangeModel(NetworkedPlayer player, string characterId)
    {
        PlayerModelInfo model = Multiplayer.PlayerModelRegistry.GetModelById(characterId);
        player.ChangeModel(model.Prefab);
    }

    public void SetPing(NetworkedPlayer player, int ping) => player.SetPing(ping);
}
