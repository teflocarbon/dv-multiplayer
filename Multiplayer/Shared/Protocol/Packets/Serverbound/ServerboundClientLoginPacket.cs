using Multiplayer.Shared.Protocol.Data;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

public class ServerboundClientLoginPacket
{
    public string Username { get; set; }
    // The install's GUID: the player ID on a host with -mp-allow-unverified, and otherwise only
    // used to find data saved under it before identities were Steam accounts.
    public byte[] Guid { get; set; }
    // The Steam account the player claims, and a session ticket proving it (PlayerAuthentication).
    // Empty without Steam.
    public ulong SteamId { get; set; }
    public byte[] AuthTicket { get; set; }
    public string Password { get; set; }
    public string BuildVersion { get; set; }
    public ModInfo[] Mods { get; set; }
    public string CharacterId { get; set; }
    public bool IsVR { get; set; }
}
