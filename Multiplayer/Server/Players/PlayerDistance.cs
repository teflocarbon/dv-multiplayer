using DV.Interaction;
using DV.KeyboardInput;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Server.Players;

/// <summary>How far the server's players are from something: the host's range and reach rules.</summary>
public static class PlayerDistance
{
    public static float AnyPlayerSqrMag(this GameObject item)
    {
        return AnyPlayerSqrMag(item.transform.position);
    }

    public static float AnyPlayerSqrMag(this Vector3 anchor)
    {
        float result = float.MaxValue;
        //string origin = new StackTrace().GetFrame(1).GetMethod().Name;

        //Loop through all of the players and return the one thats closest to the anchor
        foreach (ServerPlayer serverPlayer in NetworkServer.Instance.ServerPlayers)
        {
            float sqDist = (serverPlayer.WorldPosition - anchor).sqrMagnitude;

            if (sqDist < result)
                result = sqDist;
        }

        return result;
    }

    public static bool PlayerCanReach(this GameObject item, ServerPlayer player, float extraRange = 0f)
    {
        return PlayerCanReach (item.transform, player, extraRange);
    }

    public static bool PlayerCanReach(this Transform item, ServerPlayer player, float extraRange = 0f)
    {
        float reachRange = AKeyboardInput.XZ_SQR_REACH_RANGE + GrabberRaycasterDV.FPS_INTERACTION_RANGE_SQR + (extraRange * extraRange);

        var delta = player.WorldPosition - item.transform.position;

        if (Mathf.Abs(delta.y) > AKeyboardInput.Y_REACH_RANGE)
            return false;

        delta.y = 0f;

        float sqrMag = (delta).sqrMagnitude;

        return sqrMag <= reachRange;
    }
}
