using System;

namespace Multiplayer.Networking.Data.Items;

public class LostItemData
{
    public ushort NetId { get; set; }
    public uint Revision { get; set; }
    public string PrefabName { get; set; }
    public string DisplayName { get; set; }
    public byte Reason { get; set; }
    public long LostUtcTicks { get; set; }
}
