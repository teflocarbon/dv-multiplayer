using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.World;
using UnityEngine;



namespace Multiplayer.Shared.Utils;

public static class DvExtensions
{
    #region TrainCar

    public static ushort GetNetId(this TrainCar car)
    {
        ushort netId = 0;

        if (car != null && car.TryNetworked(out NetworkedTrainCar networkedTrainCar))
            netId = networkedTrainCar.NetId;
/*
        if (netId == 0)
            Multiplayer.LogWarning($"NetId for {car.carLivery.id} ({car.ID}) isn't initialized!\r\n" + (Multiplayer.Settings.DebugLogging ? new System.Diagnostics.StackTrace() : ""));*/
            //throw new InvalidOperationException($"NetId for {car.carLivery.id} ({car.ID}) isn't initialized!");
        return netId;
    }

    //public static NetworkedTrainCar Networked(this TrainCar trainCar)
    //{
    //    return NetworkedTrainCar.GetFromTrainCar(trainCar);
    //}

    public static bool TryNetworked(this TrainCar trainCar, out NetworkedTrainCar networkedTrainCar)
    {
        return NetworkedTrainCar.TryGetFromTrainCar(trainCar, out networkedTrainCar);
    }

    #endregion

    #region RailTrack

    public static NetworkedRailTrack Networked(this RailTrack railTrack)
    {
        return NetworkedRailTrack.GetFromRailTrack(railTrack);
    }

    #endregion

    #region Utils

    public static Vector3 GetWorldAbsolutePosition(this GameObject go)
    {
        return go.transform.GetWorldAbsolutePosition();
    }

    public static Vector3 GetWorldAbsolutePosition(this Transform transform)
    {
        return transform.position - WorldMover.currentMove;
    }

    /// <summary>Whether this game may pause: the server decides (<see cref="IPauseServer"/>), so never on a remote client.</summary>
    public static bool AllowPause()
    {
        return NetworkLifecycle.Instance.LocalServer is IPauseServer server && server.AllowsPause;
    }
    #endregion
}
