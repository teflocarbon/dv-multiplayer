using System;
using LiteNetLib.Utils;
using Multiplayer.Core.Diagnostics;

namespace Multiplayer.Networking.Data;

/// <summary>Wire form of a <see cref="ChangeCause{TReason}"/>; reason enums are byte-sized.</summary>
public static class ChangeCauseData
{
    public static void Serialize<TReason>(NetDataWriter writer, ChangeCause<TReason> cause)
        where TReason : struct, Enum
    {
        writer.Put(Convert.ToByte(cause.Reason));
        writer.Put(cause.PlayerId);
        writer.Put(cause.PlayerName);
        writer.Put(cause.Tick);
        writer.Put(cause.Detail);
    }

    public static ChangeCause<TReason> Deserialize<TReason>(NetDataReader reader)
        where TReason : struct, Enum
    {
        var reason = (TReason)Enum.ToObject(typeof(TReason), reader.GetByte());
        byte playerId = reader.GetByte();
        string playerName = reader.GetString();
        uint tick = reader.GetUInt();
        string detail = reader.GetString();
        return new ChangeCause<TReason>(reason, playerId, playerName, tick, detail);
    }
}
