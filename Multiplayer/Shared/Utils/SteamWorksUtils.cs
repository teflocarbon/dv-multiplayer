using DV.Platform.Steam;
using DV;
using Multiplayer.Shared.Protocol.Data;
using Steamworks.Data;
using Steamworks;
using System.Linq;
using System;

namespace Multiplayer.Shared.Utils;

public static class SteamworksUtils
{
    public const string LOBBY_MP_MOD_KEY = "MP_MOD";
    public const string LOBBY_NET_LOCATION_KEY = "NetLocation";
    public const string LOBBY_HAS_PASSWORD = "HasPassword";

    public static bool GetSteamUser(out string username, out ulong steamId)
    {
        username = null;
        steamId = 0;

        try
        {
            if (!DVSteamworks.Success)
                return false;

            if (!SteamClient.IsValid || !SteamClient.SteamId.IsValid)
            {
                Multiplayer.Log($"Failed to get SteamID. Status: {SteamClient.IsValid}, {SteamClient.SteamId.IsValid}");
                return false;
            }

            steamId = SteamClient.SteamId.Value;
            username = SteamClient.Name;

            if (SteamApps.IsAppInstalled(DVSteamworks.APP_ID))
                Multiplayer.Log($"Found Steam Name: {username}, steamId {steamId}");
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"Failed to obtain Steam user.\r\n{ex.StackTrace}");
        }

        return true;
    }

    public static void SetLobbyData(Lobby lobby, LobbyServerData data, string[] exclude)
    {
        var properties = typeof(LobbyServerData).GetProperties().Where(p => !exclude.Contains(p.Name));
        foreach (var prop in properties)
        {
            var value = prop.GetValue(data)?.ToString() ?? "";
            if (prop.Name == nameof(LobbyServerData.RequiredMods))
            {
                try
                {
                    value = Newtonsoft.Json.JsonConvert.SerializeObject((ModInfo[])prop.GetValue(data));
                }
                catch (Exception ex)
                {
                    Multiplayer.LogException($"SetLobbyData() Error serializing RequiredMods property", ex);
                }

                Multiplayer.LogDebug(() => $"SetLobbyData() Setting property: {prop.Name}, value: {value}");
            }
            lobby.SetData(prop.Name, value);
        }
    }

    public static LobbyServerData GetLobbyData(this Lobby lobby)
    {
        var data = new LobbyServerData();
        var properties = typeof(LobbyServerData).GetProperties();
        string value = null;

        foreach (var prop in properties)
        {
            try
            {
                value = lobby.GetData(prop.Name);
                if (string.IsNullOrEmpty(value)) continue;

                Multiplayer.LogDebug(() => $"GetLobbyData() Retrieving property: {prop.Name}, value: {value}");

                // Backward compatibility for non-JSON strings
                if (prop.Name == nameof(LobbyServerData.RequiredMods))
                {
                    var mods = ModInfo.DeserializeRequiredMods(value);

                    prop.SetValue(data, mods);
                    continue;
                }

                if (prop.PropertyType.IsEnum)
                {
                    var enumValue = Enum.Parse(prop.PropertyType, value);
                    prop.SetValue(data, enumValue);
                }
                else
                {
                    var converted = Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(data, converted);
                }

                value = null;
            }
            catch (Exception ex)
            {
                Multiplayer.LogException($"GetLobbyData() Error parsing property: {prop?.Name}, value: {value}", ex);
            }
        }

        return data;
    }
}
