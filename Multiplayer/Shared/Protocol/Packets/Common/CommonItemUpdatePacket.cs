using LiteNetLib.Utils;
using System.Collections.Generic;
using System;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Common;

public class CommonItemUpdatePacket
{
    public ItemUpdateData ItemData { get; set; }
}


