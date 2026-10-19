using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.World;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

public class ClientboundPitStopBulkUpdatePacket
{
    public ushort NetId { get; set; }
    public int CarCount { get; set; }
    public int CarSelection {  get; set; }
    public int FaucetNotch { get; set; }
    public LocoResourceModuleData[] ResourceData { get; set; }
    public PitStopPlugData[] PlugData { get; set; }
}
